using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimFlow.Data;
using ClaimFlow.Security.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaimFlow.Intake.Tests;

public class IntakeApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"claimflow-intake-{Guid.NewGuid():N}.db");

    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:ClaimFlow"] = $"Data Source={_databasePath};Pooling=False",
                ["Logging:LogLevel:Default"] = "Warning",
                ["Outbox:Enabled"] = "false",
                ["Serilog:Console"] = "false",
                ["Serilog:InMemory"] = "true",
                ["Jwt:SigningKey"] = SigningKey,
                ["Jwt:Issuer"] = "claimflow-tests",
                ["Jwt:Audience"] = "claimflow-tests-api",
            });
        });
    }

    public HttpClient CreateAuthenticatedClient(string role = ClaimFlowRoles.Admin)
    {
        var client = CreateClient();
        var token = JwtTokenFactory.CreateToken(
            new JwtOptions
            {
                Issuer = "claimflow-tests",
                Audience = "claimflow-tests-api",
                SigningKey = SigningKey,
            },
            "test-user",
            new[] { role });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public ClaimFlowDbContext CreateDbContext()
    {
        var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ClaimFlowDbContext>();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(_databasePath))
        {
            try
            {
                File.Delete(_databasePath);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp SQLite file.
            }
        }
    }
}
