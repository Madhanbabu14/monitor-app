using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Monitor.Core.Options;
using Monitor.Data.Repositories;
using Monitor.Files;
using Monitor.Files.DependencyInjection;
using Npgsql;

namespace Monitor.UnitTests.DependencyInjection;

/// <summary>
/// Exercises <see cref="FilesServiceCollectionExtensions.AddMonitorFiles"/> — registers
/// the Monitor.Files bounded context (S3 client, raw-listing memory cache,
/// <see cref="IS3FilesService"/>). Mirrors <c>IdentityServiceCollectionExtensionsTests</c>'s
/// approach of resolving purely through DI, with a hand-written <see cref="IPrimaryDb"/>
/// fake standing in for <c>Monitor.Data</c> since <c>AddMonitorFiles</c> itself never
/// registers that dependency (it's added separately by <c>AddMonitorData()</c> per the
/// Program.cs wiring order documented on the extension method).
/// </summary>
public class FilesServiceCollectionExtensionsTests
{
    private sealed class FakePrimaryDb : IPrimaryDb
    {
        public Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

        public Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(default(T));

        public Task<TResult> WithTransactionAsync<TResult>(Func<NpgsqlConnection, NpgsqlTransaction, Task<TResult>> action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TestConnectionAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static ServiceProvider BuildProvider(AwsOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AwsOptions>(o =>
        {
            o.AccessKeyId = options.AccessKeyId;
            o.SecretAccessKey = options.SecretAccessKey;
            o.Region = options.Region;
            o.S3Bucket = options.S3Bucket;
            o.S3Prefix = options.S3Prefix;
        });
        services.AddSingleton<IPrimaryDb>(new FakePrimaryDb());
        services.AddMonitorFiles();
        return services.BuildServiceProvider();
    }

    private static AwsOptions MakeOptions(string region = "us-east-1") => new()
    {
        AccessKeyId = "AKIA_TEST",
        SecretAccessKey = "secret-test",
        Region = region,
        S3Bucket = "test-bucket",
        S3Prefix = "data/",
    };

    [Fact]
    public void AddMonitorFiles_ReturnsTheSameServiceCollection_ForFluentChaining()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AwsOptions>(_ => { });
        services.AddSingleton<IPrimaryDb>(new FakePrimaryDb());

        var returned = services.AddMonitorFiles();

        Assert.Same(services, returned);
    }

    [Fact]
    public void AddMonitorFiles_RegistersIAmazonS3_ConfiguredFromAwsOptions()
    {
        using var provider = BuildProvider(MakeOptions(region: "eu-west-1"));

        var s3Client = provider.GetRequiredService<IAmazonS3>();

        Assert.IsType<AmazonS3Client>(s3Client);
        Assert.Equal(RegionEndpoint.EUWest1, s3Client.Config.RegionEndpoint);
    }

    [Fact]
    public void AddMonitorFiles_IAmazonS3_UsesConfiguredCredentials()
    {
        using var provider = BuildProvider(MakeOptions());

        // AmazonServiceClient exposes its resolved credentials only as a protected
        // member, so reach it the same way the SDK itself does at request-signing
        // time: via ImmutableCredentials on the client's protected Credentials
        // property, read through reflection since this is a black-box DI wiring test.
        var s3Client = (AmazonS3Client)provider.GetRequiredService<IAmazonS3>();
        var credentialsProperty = typeof(AmazonServiceClient).GetProperty(
            "Credentials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var awsCredentials = Assert.IsAssignableFrom<AWSCredentials>(credentialsProperty!.GetValue(s3Client));
        var credentials = awsCredentials.GetCredentials();

        Assert.Equal("AKIA_TEST", credentials.AccessKey);
        Assert.Equal("secret-test", credentials.SecretKey);
    }

    [Fact]
    public void AddMonitorFiles_IAmazonS3_IsRegisteredAsASingleton()
    {
        using var provider = BuildProvider(MakeOptions());

        var first = provider.GetRequiredService<IAmazonS3>();
        var second = provider.GetRequiredService<IAmazonS3>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddMonitorFiles_RegistersMemoryCache()
    {
        using var provider = BuildProvider(MakeOptions());

        Assert.NotNull(provider.GetRequiredService<IMemoryCache>());
    }

    [Fact]
    public void AddMonitorFiles_RegistersS3ErrorMapperAsSingleton()
    {
        using var provider = BuildProvider(MakeOptions());

        var first = provider.GetRequiredService<S3ErrorMapper>();
        var second = provider.GetRequiredService<S3ErrorMapper>();

        Assert.Same(first, second);
    }

    [Fact]
    public void AddMonitorFiles_ResolvesIS3FilesService_AsS3FilesServiceImplementation()
    {
        using var provider = BuildProvider(MakeOptions());
        using var scope = provider.CreateScope();

        var filesService = scope.ServiceProvider.GetRequiredService<IS3FilesService>();

        Assert.IsType<S3FilesService>(filesService);
    }

    [Fact]
    public void AddMonitorFiles_IS3FilesService_IsScoped_DifferentInstancePerScope()
    {
        using var provider = BuildProvider(MakeOptions());

        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var instance1 = scope1.ServiceProvider.GetRequiredService<IS3FilesService>();
        var instance2 = scope2.ServiceProvider.GetRequiredService<IS3FilesService>();
        var instance1Again = scope1.ServiceProvider.GetRequiredService<IS3FilesService>();

        Assert.NotSame(instance1, instance2);
        Assert.Same(instance1, instance1Again);
    }
}
