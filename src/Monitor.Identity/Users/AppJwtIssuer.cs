using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Monitor.Core.Domain;
using Monitor.Core.Options;
using Monitor.Identity.Authentication;

namespace Monitor.Identity.Users;

/// <inheritdoc cref="IAppJwtIssuer"/>
public sealed class AppJwtIssuer : IAppJwtIssuer
{
    private readonly JwtOptions _options;

    public AppJwtIssuer(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public string IssueToken(string subject, string email, string displayName, UserRole role, string? azureOid = null)
    {
        var claims = new List<Claim>
        {
            new(JwtClaimTypes.Subject, subject),
            new(JwtClaimTypes.Email, email),
            new(JwtClaimTypes.Name, displayName),
            new(JwtClaimTypes.Role, role.ToWireString()),
        };

        if (!string.IsNullOrEmpty(azureOid))
        {
            claims.Add(new Claim(JwtClaimTypes.ObjectId, azureOid));
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.Add(JwtExpiry.Parse(_options.ExpiresIn)),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
