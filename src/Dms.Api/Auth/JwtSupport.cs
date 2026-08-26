using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dms.Application.Abstractions;
using Dms.Domain.Entities;
using Microsoft.IdentityModel.Tokens;
using Dms.Domain.Common;

namespace Dms.Api.Auth;

public static class JwtConfig
{
    public const string SectionName = "Jwt";

    public const string SigningKeyKey = $"{SectionName}:SigningKey";
    public const string IssuerKey = $"{SectionName}:Issuer";
    public const string AudienceKey = $"{SectionName}:Audience";
    public const string TokenMinutesKey = $"{SectionName}:TokenMinutes";
    public const string FailedLoginThresholdKey = $"{SectionName}:FailedLoginThreshold";
    public const string LockoutMinutesKey = $"{SectionName}:LockoutMinutes";

    public const string DefaultIssuer = "dms";
    public const string DefaultAudience = "dms-api";

    /// <summary>
    /// Reads the signing key, refusing anything too short to be safe.
    /// <para>
    /// HMAC-SHA256 keys shorter than 256 bits weaken the signature, and a guessable key here
    /// means anyone can mint a token for any user — including one that passes every permission
    /// check in the system. Failing at startup is the right response.
    /// </para>
    /// </summary>
    public static SymmetricSecurityKey ReadSigningKey(IConfiguration configuration)
    {
        var key = configuration[SigningKeyKey];

        return string.IsNullOrWhiteSpace(key) || key.Length < 32
            ? throw new InvalidOperationException(
                $"{SigningKeyKey} must be set to at least 32 characters.")
            : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }
}

public sealed class AuthPolicy(IConfiguration configuration) : IAuthPolicy
{
    public int FailedLoginThreshold { get; } =
        Math.Clamp(configuration.GetValue(JwtConfig.FailedLoginThresholdKey, 5), 3, 20);

    public TimeSpan LockoutDuration { get; } =
        TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue(JwtConfig.LockoutMinutesKey, 15), 1, 1440));

    public TimeSpan TokenLifetime { get; } =
        TimeSpan.FromMinutes(Math.Clamp(configuration.GetValue(JwtConfig.TokenMinutesKey, 60), 5, 720));
}

public sealed class JwtAccessTokenIssuer(IConfiguration configuration) : IAccessTokenIssuer
{
    private readonly SymmetricSecurityKey _key = JwtConfig.ReadSigningKey(configuration);
    private readonly string _issuer = configuration[JwtConfig.IssuerKey] ?? JwtConfig.DefaultIssuer;
    private readonly string _audience = configuration[JwtConfig.AudienceKey] ?? JwtConfig.DefaultAudience;

    public string Issue(DmsUser user, DateTimeOffset expiresAt)
    {
        // Identity only — no roles, no permissions. Those are evaluated live on every request
        // by IAccessControl, so revoking a role takes effect immediately rather than whenever
        // the token happens to expire.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("N")),
            new(ClaimTypes.Name, user.UserName),
            new("full_name", user.FullName),
            new(JwtRegisteredClaimNames.Jti, Uuid7.NewGuid().ToString("N")),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
