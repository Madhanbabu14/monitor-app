using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Api.Endpoints;
using Monitor.Core.Domain;
using Monitor.Core.Errors;
using Monitor.Identity.Authentication;
using Monitor.Identity.Authorization;
using Monitor.Identity.Users;

namespace Monitor.UnitTests.Endpoints;

/// <summary>
/// Exercises <see cref="AuthEndpoints.MapAuthEndpoints"/> — the .NET collapse of
/// auth.routes.ts's route wiring (validation + response envelope) around
/// <see cref="IAuthService"/> (business logic is covered separately by
/// <c>AuthServiceTests</c>). Boots a minimal, DB-free <see cref="WebApplication"/> with a
/// fake <see cref="IAuthService"/> and a controllable test authentication handler for GET
/// /api/auth/me, instead of the real Monitor.Api <c>Program.cs</c> (out of scope for unit
/// tests per <c>MiddlewarePipelineOrderTests</c> — it requires a reachable Postgres).
///
/// Requests are dispatched by building the configured pipeline directly
/// (<c>((IApplicationBuilder)app).Build()</c>) and invoking it against a
/// <see cref="DefaultHttpContext"/>, rather than going through
/// <c>Microsoft.AspNetCore.TestHost.TestServer</c> + <see cref="HttpClient"/>: TestServer's
/// bespoke response-body <see cref="System.IO.Pipelines.PipeWriter"/> doesn't implement the
/// newer <c>PipeWriter.UnflushedBytes</c> member current <c>System.Text.Json</c>
/// (<c>Results.Json</c> / <c>WriteAsJsonAsync</c>) requires, so JSON responses fail to
/// serialize under TestServer on this SDK. Driving the pipeline directly uses the
/// framework's own <c>StreamPipeWriter</c> over a plain <see cref="MemoryStream"/> instead,
/// which has no such gap, while still exercising the endpoint module's real routing,
/// authentication/authorization middleware, and response-writing code.
///
/// A tiny inline exception-translating middleware mirrors <see cref="AppException"/>'s
/// documented `{"status":"error","message":"..."}` contract purely so responses stay
/// assertable; it is test infrastructure only — the real <c>ExceptionHandlingMiddleware</c>
/// (a different slice) is not under test here.
///
/// Two additional pieces of plumbing stand in for behavior <c>Kestrel</c> normally provides
/// for free: an explicit <c>app.UseRouting()</c>/<c>app.UseEndpoints(_ =&gt; {})</c> pair
/// (driving the pipeline via a raw <see cref="RequestDelegate"/> bypasses the automatic
/// routing/endpoint-middleware insertion <see cref="WebApplication"/> normally performs when
/// started through the host), and a fake <see cref="Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature"/>
/// (minimal APIs' generated request delegates consult this feature to decide whether a
/// complex parameter such as the internal <c>LoginRequest?</c>/<c>SsoRequest?</c> may be
/// bound from the request body; <see cref="DefaultHttpContext"/> never populates it, so
/// without this stand-in every JSON body silently binds to <see langword="null"/>).
/// </summary>
public class AuthEndpointsTests
{
    private sealed class FakeAuthService : IAuthService
    {
        public Func<string, string, (AuthenticatedUser User, string Token)>? OnLogin;
        public Func<string, (AuthenticatedUser User, string Token)>? OnSso;
        public (string Email, string Password)? LastLoginArgs { get; private set; }
        public string? LastSsoToken { get; private set; }

        public Task<(AuthenticatedUser User, string Token)> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        {
            LastLoginArgs = (email, password);
            return Task.FromResult(OnLogin!(email, password));
        }

        public Task<(AuthenticatedUser User, string Token)> SsoLoginAsync(string azureToken, CancellationToken cancellationToken = default)
        {
            LastSsoToken = azureToken;
            return Task.FromResult(OnSso!(azureToken));
        }
    }

    private sealed class PrincipalHolder
    {
        public ClaimsPrincipal? Principal;
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private readonly PrincipalHolder _holder;

        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PrincipalHolder holder)
            : base(options, logger, encoder)
        {
            _holder = holder;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (_holder.Principal is null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var ticket = new AuthenticationTicket(_holder.Principal, "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed record HttpCallResult(int StatusCode, JsonElement? Body);

    private sealed class BodyDetectionFeature : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature
    {
        public BodyDetectionFeature(bool canHaveBody) => CanHaveBody = canHaveBody;

        public bool CanHaveBody { get; }
    }

    private sealed class TestApp : IAsyncDisposable
    {
        public TestApp(WebApplication app, RequestDelegate pipeline, PrincipalHolder principal)
        {
            App = app;
            Pipeline = pipeline;
            Principal = principal;
        }

        public WebApplication App { get; }
        public RequestDelegate Pipeline { get; }
        public PrincipalHolder Principal { get; }

        public async Task<HttpCallResult> SendAsync(string method, string path, object? jsonBody = null)
        {
            var context = new DefaultHttpContext
            {
                RequestServices = App.Services,
            };
            context.Request.Method = method;
            context.Request.Path = path;
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("localhost");

            if (jsonBody is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(jsonBody);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = bytes.Length;
            }

            // DefaultHttpContext doesn't populate IHttpRequestBodyDetectionFeature the way
            // Kestrel does. Minimal APIs' generated request delegates consult this feature
            // to decide whether a complex parameter (e.g. LoginRequest?) may be bound from
            // the request body; without it explicitly saying "yes", the body is never read
            // and the parameter is left null. Stand in for Kestrel here so the endpoint's
            // real JSON-body binding logic actually executes end to end.
            context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(
                new BodyDetectionFeature(jsonBody is not null));

            var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            await Pipeline(context);

            responseBody.Position = 0;
            var text = Encoding.UTF8.GetString(responseBody.ToArray());
            JsonElement? body = text.Length > 0 ? JsonSerializer.Deserialize<JsonElement>(text) : null;
            return new HttpCallResult(context.Response.StatusCode, body);
        }

        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    private static TestApp CreateApp(IAuthService authService)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var holder = new PrincipalHolder();
        builder.Services.AddSingleton(holder);
        builder.Services.AddSingleton(authService);
        builder.Services
            .AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", null);
        builder.Services.AddAuthorization(options =>
            options.AddPolicy(AuthorizationPolicyNames.AuthenticatedUser, policy => policy.RequireAuthenticatedUser()));

        var app = builder.Build();

        // Test-only stand-in for the real ExceptionHandlingMiddleware (a different
        // slice): renders AppException per its documented envelope so the endpoint's
        // validation/error paths are assertable.
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (AppException ex)
            {
                context.Response.StatusCode = ex.StatusCode;
                await context.Response.WriteAsJsonAsync(new { status = "error", message = ex.Message });
            }
        });

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAuthEndpoints();
        app.UseEndpoints(_ => { });

        var pipeline = ((IApplicationBuilder)app).Build();
        return new TestApp(app, pipeline, holder);
    }

    private static AuthenticatedUser MakeUser(
        string id = "user-1",
        string email = "alice@example.com",
        string displayName = "Alice Example",
        UserRole role = UserRole.Admin,
        string azureOid = "") =>
        new(id, email, displayName, role, azureOid);

    // ── POST /api/auth/login ────────────────────────────────────────────────

    [Fact]
    public async Task Login_MissingEmail_Returns400WithExpectedMessage()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/login", new { password = "secret" });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("error", response.Body!.Value.GetProperty("status").GetString());
        Assert.Equal("Email and password are required", response.Body!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Login_MissingPassword_Returns400WithExpectedMessage()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/login", new { email = "alice@example.com" });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("Email and password are required", response.Body!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Login_EmptyBody_Returns400()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/login", new { });

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task Login_EmptyStringEmailAndPassword_Returns400()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/login", new { email = "", password = "" });

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task Login_ValidRequest_PassesEmailAndPasswordToAuthService()
    {
        var authService = new FakeAuthService
        {
            OnLogin = (_, _) => (MakeUser(), "token-abc"),
        };
        await using var testApp = CreateApp(authService);

        await testApp.SendAsync("POST", "/api/auth/login", new { email = "alice@example.com", password = "hunter2" });

        Assert.Equal(("alice@example.com", "hunter2"), authService.LastLoginArgs);
    }

    [Fact]
    public async Task Login_Success_ReturnsSuccessEnvelopeWithMappedUserAndToken()
    {
        var authService = new FakeAuthService
        {
            OnLogin = (_, _) => (MakeUser(id: "user-42", email: "alice@example.com", displayName: "Alice Example", role: UserRole.Admin, azureOid: ""), "jwt-token-value"),
        };
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/login", new { email = "alice@example.com", password = "hunter2" });

        Assert.Equal(200, response.StatusCode);
        var body = response.Body!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var data = body.GetProperty("data");
        Assert.Equal("jwt-token-value", data.GetProperty("token").GetString());
        var user = data.GetProperty("user");
        Assert.Equal("user-42", user.GetProperty("id").GetString());
        Assert.Equal("alice@example.com", user.GetProperty("email").GetString());
        Assert.Equal("Alice Example", user.GetProperty("displayName").GetString());
        Assert.Equal("Admin", user.GetProperty("role").GetString());
        Assert.Equal(string.Empty, user.GetProperty("azureOid").GetString());
        // password/hash/isActive must never appear on the wire.
        Assert.False(user.TryGetProperty("passwordHash", out _));
        Assert.False(user.TryGetProperty("isActive", out _));
    }

    [Theory]
    [InlineData(401, "Invalid email or password")]
    [InlineData(403, "Account is inactive. Contact your administrator.")]
    public async Task Login_AuthServiceThrowsAppException_PropagatesStatusCodeAndMessage(int statusCode, string message)
    {
        var authService = new FakeAuthService
        {
            OnLogin = (_, _) => throw new AppException(statusCode, message),
        };
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/login", new { email = "alice@example.com", password = "wrong" });

        Assert.Equal(statusCode, response.StatusCode);
        var body = response.Body!.Value;
        Assert.Equal("error", body.GetProperty("status").GetString());
        Assert.Equal(message, body.GetProperty("message").GetString());
    }

    // ── POST /api/auth/sso ──────────────────────────────────────────────────

    [Fact]
    public async Task Sso_MissingAzureToken_Returns400WithExpectedMessage()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/sso", new { });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("azureToken is required", response.Body!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Sso_EmptyStringAzureToken_Returns400()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/sso", new { azureToken = "" });

        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task Sso_ValidRequest_PassesTokenToAuthService()
    {
        var authService = new FakeAuthService
        {
            OnSso = _ => (MakeUser(), "sso-token"),
        };
        await using var testApp = CreateApp(authService);

        await testApp.SendAsync("POST", "/api/auth/sso", new { azureToken = "raw-azure-jwt" });

        Assert.Equal("raw-azure-jwt", authService.LastSsoToken);
    }

    [Fact]
    public async Task Sso_Success_ReturnsSuccessEnvelopeWithAzureOidPopulated()
    {
        var authService = new FakeAuthService
        {
            OnSso = _ => (MakeUser(id: "user-7", role: UserRole.Viewer, azureOid: "azure-oid-99"), "app-jwt"),
        };
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/sso", new { azureToken = "raw-azure-jwt" });

        Assert.Equal(200, response.StatusCode);
        var body = response.Body!.Value;
        var user = body.GetProperty("data").GetProperty("user");
        Assert.Equal("azure-oid-99", user.GetProperty("azureOid").GetString());
        Assert.Equal("Viewer", user.GetProperty("role").GetString());
        Assert.Equal("app-jwt", body.GetProperty("data").GetProperty("token").GetString());
    }

    [Fact]
    public async Task Sso_AuthServiceThrows401_PropagatesAzureTokenValidationFailure()
    {
        var authService = new FakeAuthService
        {
            OnSso = _ => throw new AppException(401, "Azure AD token validation failed: signature mismatch"),
        };
        await using var testApp = CreateApp(authService);

        var response = await testApp.SendAsync("POST", "/api/auth/sso", new { azureToken = "bad-token" });

        Assert.Equal(401, response.StatusCode);
        Assert.Equal("Azure AD token validation failed: signature mismatch", response.Body!.Value.GetProperty("message").GetString());
    }

    // ── GET /api/auth/me ────────────────────────────────────────────────────

    [Fact]
    public async Task Me_Unauthenticated_Returns401()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);
        testApp.Principal.Principal = null;

        var response = await testApp.SendAsync("GET", "/api/auth/me");

        Assert.Equal(401, response.StatusCode);
    }

    [Fact]
    public async Task Me_Authenticated_ReturnsMappedUserFromClaimsPrincipal()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("sub", "user-9"),
            new Claim("email", "carol@example.com"),
            new Claim("name", "Carol Example"),
            new Claim("role", "Operator"),
            new Claim("oid", "azure-oid-5"),
        }, "Test");
        testApp.Principal.Principal = new ClaimsPrincipal(identity);

        var response = await testApp.SendAsync("GET", "/api/auth/me");

        Assert.Equal(200, response.StatusCode);
        var body = response.Body!.Value;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var user = body.GetProperty("data");
        Assert.Equal("user-9", user.GetProperty("id").GetString());
        Assert.Equal("carol@example.com", user.GetProperty("email").GetString());
        Assert.Equal("Carol Example", user.GetProperty("displayName").GetString());
        Assert.Equal("Operator", user.GetProperty("role").GetString());
        Assert.Equal("azure-oid-5", user.GetProperty("azureOid").GetString());
    }

    [Fact]
    public async Task Me_AuthenticatedWithNoRoleClaim_DefaultsToViewer()
    {
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);
        var identity = new ClaimsIdentity(new[] { new Claim("sub", "user-1") }, "Test");
        testApp.Principal.Principal = new ClaimsPrincipal(identity);

        var response = await testApp.SendAsync("GET", "/api/auth/me");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Viewer", response.Body!.Value.GetProperty("data").GetProperty("role").GetString());
    }

    [Fact]
    public async Task Me_DoesNotCallAuthService()
    {
        // GET /api/auth/me (auth.routes.ts) reads straight off `req.user` — it never
        // touches AuthService.
        var authService = new FakeAuthService();
        await using var testApp = CreateApp(authService);
        testApp.Principal.Principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-1") }, "Test"));

        await testApp.SendAsync("GET", "/api/auth/me");

        Assert.Null(authService.LastLoginArgs);
        Assert.Null(authService.LastSsoToken);
    }
}
