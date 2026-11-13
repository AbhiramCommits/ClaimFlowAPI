using System.Net.Http.Headers;
using System.Text;
using ClaimFlow.Security.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ClaimFlow.Security.Auth;

public static class SecurityRegistration
{
    public static IServiceCollection AddClaimFlowSecurity(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var section = configuration.GetSection(JwtOptions.SectionName);
                options.Issuer = section["Issuer"] ?? options.Issuer;
                options.Audience = section["Audience"] ?? options.Audience;
                options.SigningKey = section["SigningKey"] ?? options.SigningKey;
                if (int.TryParse(section["LifetimeMinutes"], out var lifetime))
                {
                    options.LifetimeMinutes = lifetime;
                }
            });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                var options = jwt.Value;
                if (Encoding.UTF8.GetByteCount(options.SigningKey) < 32)
                {
                    throw new InvalidOperationException(
                        "Jwt:SigningKey is missing or shorter than 32 bytes. Set Jwt__SigningKey (or JWT__SIGNINGKEY).");
                }

                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ClaimFlowPolicies.ClaimsWrite, policy =>
                policy.RequireAssertion(context => HasAnyRole(context.User, ClaimFlowRoles.ClaimsWriter)));
            options.AddPolicy(ClaimFlowPolicies.ClaimsRead, policy =>
                policy.RequireAssertion(context => HasAnyRole(
                    context.User,
                    ClaimFlowRoles.ClaimsWriter,
                    ClaimFlowRoles.Adjudicator,
                    ClaimFlowRoles.ReportingReader)));
            options.AddPolicy(ClaimFlowPolicies.Adjudicate, policy =>
                policy.RequireAssertion(context => HasAnyRole(context.User, ClaimFlowRoles.Adjudicator)));
            options.AddPolicy(ClaimFlowPolicies.ReportingRead, policy =>
                policy.RequireAssertion(context => HasAnyRole(context.User, ClaimFlowRoles.ReportingReader)));
            options.AddPolicy(ClaimFlowPolicies.AdminOnly, policy =>
                policy.RequireAssertion(context =>
                    context.User.Identity?.IsAuthenticated == true && context.User.IsInRole(ClaimFlowRoles.Admin)));
        });

        services.AddSingleton<ServiceTokenProvider>();
        services.AddTransient<ServiceAuthHandler>();
        return services;
    }

    private static bool HasAnyRole(
        System.Security.Claims.ClaimsPrincipal user,
        params string[] roles) =>
        user.Identity?.IsAuthenticated == true &&
        (user.IsInRole(ClaimFlowRoles.Admin) || roles.Any(user.IsInRole));
}

/// <summary>Mints and caches a short-lived service token for adjudication-to-intake calls.</summary>
public sealed class ServiceTokenProvider
{
    private readonly JwtOptions _options;
    private readonly object _gate = new();
    private string? _token;
    private DateTimeOffset _expiresAt;

    public ServiceTokenProvider(IOptions<JwtOptions> options) => _options = options.Value;

    public string GetToken(string subject, string role)
    {
        lock (_gate)
        {
            if (_token is not null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return _token;
            }

            _token = JwtTokenFactory.CreateToken(
                _options,
                subject,
                new[] { role },
                TimeSpan.FromMinutes(Math.Min(15, _options.LifetimeMinutes)));
            _expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Min(15, _options.LifetimeMinutes));
            return _token;
        }
    }
}

/// <summary>Attaches a service bearer token to outbound adjudication-to-intake requests.</summary>
public sealed class ServiceAuthHandler : DelegatingHandler
{
    private readonly ServiceTokenProvider _tokens;

    public ServiceAuthHandler(ServiceTokenProvider tokens) => _tokens = tokens;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                _tokens.GetToken("claimflow-adjudication", ClaimFlowRoles.Adjudicator));
        }

        return base.SendAsync(request, cancellationToken);
    }
}
