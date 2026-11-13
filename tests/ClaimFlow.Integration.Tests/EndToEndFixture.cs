using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimFlow.Adjudication;
using ClaimFlow.Adjudication.Services;
using ClaimFlow.Intake;
using ClaimFlow.Security.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaimFlow.Integration.Tests;

public static class IntegrationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class IntakeServiceFactory : WebApplicationFactory<IntakeApiMarker>
{
    private readonly string _databasePath;
    private readonly string _hubName;
    private readonly string _signingKey;

    public IntakeServiceFactory(string databasePath, string hubName, string signingKey)
    {
        _databasePath = databasePath;
        _hubName = hubName;
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
                ["ConnectionStrings:ClaimFlow"] = $"Data Source={_databasePath};Pooling=False;Default Timeout=30",
                ["Messaging:Provider"] = "InMemory",
                ["Messaging:InMemoryHubName"] = _hubName,
                ["Outbox:Enabled"] = "true",
                ["Outbox:PollIntervalMs"] = "50",
                ["Logging:LogLevel:Default"] = "Warning",
                ["Serilog:Console"] = "false",
                ["Serilog:InMemory"] = "true",
                ["Jwt:SigningKey"] = _signingKey,
                ["Jwt:Issuer"] = "claimflow-tests",
                ["Jwt:Audience"] = "claimflow-tests-api",
            });
        });
    }
}

public sealed class AdjudicationServiceFactory : WebApplicationFactory<AdjudicationApiMarker>
{
    private readonly string _databasePath;
    private readonly string _hubName;
    private readonly string _signingKey;
    private readonly IntakeServiceFactory _intake;

    public AdjudicationServiceFactory(
        string databasePath,
        string hubName,
        IntakeServiceFactory intake,
        string signingKey)
    {
        _databasePath = databasePath;
        _hubName = hubName;
        _intake = intake;
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
                ["ConnectionStrings:ClaimFlow"] = $"Data Source={_databasePath};Pooling=False;Default Timeout=30",
                ["Messaging:Provider"] = "InMemory",
                ["Messaging:InMemoryHubName"] = _hubName,
                ["Intake:BaseUrl"] = "http://intake.test",
                ["Logging:LogLevel:Default"] = "Warning",
                ["Serilog:Console"] = "false",
                ["Serilog:InMemory"] = "true",
                ["Jwt:SigningKey"] = _signingKey,
                ["Jwt:Issuer"] = "claimflow-tests",
                ["Jwt:Audience"] = "claimflow-tests-api",
            });
        });

        builder.ConfigureTestServices(services =>
        {
            services
                .AddHttpClient<IntakeStatusClient>(client => client.BaseAddress = new Uri("http://intake.test/"))
                .ConfigurePrimaryHttpMessageHandler(() => _intake.Server.CreateHandler())
                .AddHttpMessageHandler<ServiceAuthHandler>();
        });
    }
}

public sealed class EndToEndFixture : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"claimflow-e2e-{Guid.NewGuid():N}.db");

    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public IntakeServiceFactory Intake { get; private set; } = null!;

    public AdjudicationServiceFactory Adjudication { get; private set; } = null!;

    public HttpClient IntakeClient { get; private set; } = null!;

    public HttpClient AdjudicationClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var hubName = $"e2e-{Guid.NewGuid():N}";
        Intake = new IntakeServiceFactory(_databasePath, hubName, SigningKey);
        IntakeClient = Intake.CreateClient();
        IntakeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", Token(ClaimFlowRoles.Admin));

        Adjudication = new AdjudicationServiceFactory(_databasePath, hubName, Intake, SigningKey);
        AdjudicationClient = Adjudication.CreateClient();
        AdjudicationClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", Token(ClaimFlowRoles.Adjudicator));

        await WaitUntilAsync(
            async () => (await IntakeClient.GetAsync("/health/ready")).IsSuccessStatusCode &&
                        (await AdjudicationClient.GetAsync("/health/ready")).IsSuccessStatusCode,
            TimeSpan.FromSeconds(15));
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

    public string Token(string role) => JwtTokenFactory.CreateToken(
        new JwtOptions
        {
            Issuer = "claimflow-tests",
            Audience = "claimflow-tests-api",
            SigningKey = SigningKey,
        },
        "integration-test",
        new[] { role });

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition was not met within {timeout}.");
    }
}
