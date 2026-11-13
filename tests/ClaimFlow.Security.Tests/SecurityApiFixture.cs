using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimFlow.Adjudication;
using ClaimFlow.Intake;
using ClaimFlow.Security.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaimFlow.Security.Tests;

public static class SecurityJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class SecurityIntakeFactory : WebApplicationFactory<IntakeApiMarker>
{
    private readonly string _databasePath;
    private readonly string _signingKey;

    public SecurityIntakeFactory(string databasePath, string signingKey)
    {
        _databasePath = databasePath;
        _signingKey = signingKey;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:ClaimFlow"] = $"Data Source={_databasePath};Pooling=False",
                ["Messaging:Provider"] = "InMemory",
                ["Messaging:InMemoryHubName"] = "security-tests",
                ["Outbox:Enabled"] = "false",
                ["Serilog:Console"] = "false",
                ["Serilog:InMemory"] = "true",
                ["Jwt:SigningKey"] = _signingKey,
                ["Jwt:Issuer"] = "claimflow-security-tests",
                ["Jwt:Audience"] = "claimflow-security-tests-api",
            });
        });
    }
}

public sealed class SecurityAdjudicationFactory : WebApplicationFactory<AdjudicationApiMarker>
{
    private readonly string _databasePath;
    private readonly string _signingKey;

    public SecurityAdjudicationFactory(string databasePath, string signingKey)
    {
        _databasePath = databasePath;
        _signingKey = signingKey;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:ClaimFlow"] = $"Data Source={_databasePath};Pooling=False",
                ["Messaging:Provider"] = "InMemory",
                ["Messaging:InMemoryHubName"] = "security-tests",
                ["Intake:BaseUrl"] = "http://intake.invalid",
                ["Serilog:Console"] = "false",
                ["Serilog:InMemory"] = "true",
                ["Jwt:SigningKey"] = _signingKey,
                ["Jwt:Issuer"] = "claimflow-security-tests",
                ["Jwt:Audience"] = "claimflow-security-tests-api",
            });
        });
    }
}

public sealed class SecurityApiFixture : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"claimflow-security-{Guid.NewGuid():N}.db");

    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public SecurityIntakeFactory Intake { get; private set; } = null!;

    public SecurityAdjudicationFactory Adjudication { get; private set; } = null!;

    public string ClaimId => "SEC-MATRIX-CLAIM";

    public async Task InitializeAsync()
    {
        Intake = new SecurityIntakeFactory(_databasePath, SigningKey);
        Adjudication = new SecurityAdjudicationFactory(_databasePath, SigningKey);

        var admin = CreateClient("intake", ClaimFlowRoles.Admin);
        var bundle = SecurityTestData.Bundle("matrix", ClaimId);
        var response = await admin.PostAsJsonAsync("/api/claims", bundle, SecurityJson.Options);
        response.EnsureSuccessStatusCode();

        var adjudication = CreateClient("adjudication", ClaimFlowRoles.Adjudicator);
        using var ready = await adjudication.GetAsync("/health/ready");
        ready.EnsureSuccessStatusCode();
    }

    public HttpClient CreateClient(string service, string? role)
    {
        var client = service == "intake" ? Intake.CreateClient() : Adjudication.CreateClient();
        if (role is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                JwtTokenFactory.CreateToken(
                    new JwtOptions
                    {
                        Issuer = "claimflow-security-tests",
                        Audience = "claimflow-security-tests-api",
                        SigningKey = SigningKey,
                    },
                    "security-test",
                    new[] { role }));
        }

        return client;
    }

    public Task DisposeAsync()
    {
        Adjudication?.Dispose();
        Intake?.Dispose();
        if (File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        return Task.CompletedTask;
    }
}
