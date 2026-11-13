using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ClaimFlow.Security.Auth;

public static class ClaimFlowRoles
{
    public const string ClaimsWriter = "claims.writer";
    public const string Adjudicator = "adjudicator";
    public const string ReportingReader = "reporting.reader";
    public const string Admin = "admin";

    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        ClaimsWriter,
        Adjudicator,
        ReportingReader,
        Admin,
    };
}

public static class ClaimFlowPolicies
{
    public const string ClaimsWrite = "ClaimsWrite";
    public const string ClaimsRead = "ClaimsRead";
    public const string Adjudicate = "Adjudicate";
    public const string ReportingRead = "ReportingRead";
    public const string AdminOnly = "AdminOnly";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "claimflow";

    public string Audience { get; set; } = "claimflow-api";

    /// <summary>Supplied through Jwt__SigningKey / JWT__SIGNINGKEY; never committed.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int LifetimeMinutes { get; set; } = 60;
}

public static class JwtTokenFactory
{
    public static string CreateToken(
        JwtOptions options,
        string subject,
        IEnumerable<string> roles,
        TimeSpan? lifetime = null)
    {
        if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
        {
            throw new InvalidOperationException("JWT signing key must be at least 32 bytes.");
        }

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(roles.Distinct(StringComparer.Ordinal).Select(role => new Claim("role", role)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTimeOffset.UtcNow;
        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(lifetime ?? TimeSpan.FromMinutes(options.LifetimeMinutes)).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static bool IsAdmin(ClaimsPrincipal user) => user.IsInRole(ClaimFlowRoles.Admin);
}
