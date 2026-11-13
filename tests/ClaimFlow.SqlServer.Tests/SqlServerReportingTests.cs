using System.Data;
using System.Data.Common;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimFlow.Data;
using ClaimFlow.Intake;
using ClaimFlow.Security.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace ClaimFlow.SqlServer.Tests;

/// <summary>
/// Runs against a real SQL Server when CLAIMFLOW_SQLSERVER_CONNECTION is set.
/// CI sets it to its SQL Server service container; local runs without it skip the assertions.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string ClaimId = "SQL-E2E-0001";
    public const string MemberId = "MBR-SQL-E2E";
    private readonly string? _connectionString =
        Environment.GetEnvironmentVariable("CLAIMFLOW_SQLSERVER_CONNECTION");

    private string _signingKey = string.Empty;
    private WebApplicationFactory<IntakeApiMarker>? _factory;

    public bool Available => !string.IsNullOrWhiteSpace(_connectionString);

    public HttpClient IntakeClient { get; private set; } = null!;

    public HttpClient AdjudicatorClient { get; private set; } = null!;

    public HttpClient ReportingClient { get; private set; } = null!;

    public string SigningKey => _signingKey;

    public async Task InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        _signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        await DropAndMigrateAsync(_connectionString!);
        await ApplyScriptsAsync(_connectionString!);

        _factory = new SqlServerIntakeFactory(_connectionString!, _signingKey);
        IntakeClient = _factory.CreateClient();
        IntakeClient.DefaultRequestHeaders.Authorization = Bearer(ClaimFlowRoles.ClaimsWriter);
        AdjudicatorClient = _factory.CreateClient();
        AdjudicatorClient.DefaultRequestHeaders.Authorization = Bearer(ClaimFlowRoles.Adjudicator);
        ReportingClient = _factory.CreateClient();
        ReportingClient.DefaultRequestHeaders.Authorization = Bearer(ClaimFlowRoles.ReportingReader);

        using var ready = await IntakeClient.GetAsync("/health/ready");
        ready.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private AuthenticationHeaderValue Bearer(string role) => new(
        "Bearer",
        JwtTokenFactory.CreateToken(
            new JwtOptions
            {
                Issuer = "claimflow-sql-tests",
                Audience = "claimflow-sql-tests-api",
                SigningKey = _signingKey,
            },
            "sql-tests",
            new[] { role }));

    private static async Task DropAndMigrateAsync(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var database = builder["Database"]?.ToString() ?? "ClaimFlowCi";
        if (!database.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException("Test database name must be alphanumeric for the reset.");
        }

        builder["Database"] = "master";
        await using (var connection = new SqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                IF DB_ID(N'{database}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{database}];
                END
                """;
            await command.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ClaimFlowDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var db = new ClaimFlowDbContext(options);
        await db.Database.MigrateAsync();
    }

    private static async Task ApplyScriptsAsync(string connectionString)
    {
        var root = FindRoot();
        foreach (var file in new[]
                 {
                     "db/001_schema_notes.sql",
                     "db/010_views.sql",
                     "db/020_procs.sql",
                     "db/030_indexes.sql",
                 })
        {
            var script = await File.ReadAllTextAsync(Path.Combine(root, file));
            foreach (var batch in SplitOnGo(script))
            {
                if (string.IsNullOrWhiteSpace(batch))
                {
                    continue;
                }

                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClaimFlowAPI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Run the test from the repository root.");
    }

    private static IEnumerable<string> SplitOnGo(string script)
    {
        var batch = new System.Text.StringBuilder();
        using var reader = new StringReader(script);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                yield return batch.ToString();
                batch.Clear();
                continue;
            }

            batch.AppendLine(line);
        }

        yield return batch.ToString();
    }
}

public sealed class SqlServerIntakeFactory : WebApplicationFactory<IntakeApiMarker>
{
    private readonly string _connectionString;
    private readonly string _signingKey;

    public SqlServerIntakeFactory(string connectionString, string signingKey)
    {
        _connectionString = connectionString;
        _signingKey = signingKey;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "SqlServer",
                ["ConnectionStrings:ClaimFlow"] = _connectionString,
                ["Messaging:Provider"] = "InMemory",
                ["Messaging:InMemoryHubName"] = $"sql-{Guid.NewGuid():N}",
                ["Outbox:Enabled"] = "false",
                ["Serilog:Console"] = "false",
                ["Serilog:InMemory"] = "true",
                ["Jwt:SigningKey"] = _signingKey,
                ["Jwt:Issuer"] = "claimflow-sql-tests",
                ["Jwt:Audience"] = "claimflow-sql-tests-api",
            });
        });
    }
}

public sealed class SqlServerReportingTests : IClassFixture<SqlServerFixture>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqlServerFixture _fixture;

    public SqlServerReportingTests(SqlServerFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "SqlServer")]
    public async Task Reporting_procs_run_against_real_sql_server()
    {
        if (!_fixture.Available)
        {
            return;
        }

        var bundle = Bundle();
        var created = await _fixture.IntakeClient.PostAsJsonAsync("/api/claims", bundle, Json);
        created.EnsureSuccessStatusCode();

        var updated = await _fixture.AdjudicatorClient.PutAsJsonAsync(
            $"/api/claims/{SqlServerFixture.ClaimId}/status",
            new
            {
                status = "Paid",
                outcome = "PartiallyPaid",
                denialReasonCode = "PR1",
                allowedCents = 8_000,
                paidCents = 7_000,
                patientResponsibilityCents = 1_000,
                reason = "deductible",
            },
            Json);
        updated.EnsureSuccessStatusCode();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var denials = await _fixture.ReportingClient.GetFromJsonAsync<List<ClaimFlow.Shared.Reporting.DenialRollupRow>>(
            $"/api/v1/reports/denials?fromDate={today.AddDays(-30):yyyy-MM-dd}&toDate={today.AddDays(1):yyyy-MM-dd}",
            Json);
        denials.Should().NotBeNull();
        denials!.Should().ContainSingle(r => r.DenialCode == "PR1" && r.ClaimCount == 1);

        var ar = await _fixture.ReportingClient.GetFromJsonAsync<List<ClaimFlow.Shared.Reporting.DaysInArRow>>(
            $"/api/v1/reports/days-in-ar?asOfDate={today:yyyy-MM-dd}",
            Json);
        ar.Should().NotBeNull();
        var arRows = ar!;
        arRows.Sum(r => r.OpenClaimCount).Should().Be(1);
        arRows.Sum(r => r.OpenArCents).Should().Be(1_000);

        var throughput = await _fixture.ReportingClient.GetFromJsonAsync<List<ClaimFlow.Shared.Reporting.ThroughputRow>>(
            $"/api/v1/reports/throughput?fromDate={today.AddDays(-1):yyyy-MM-dd}&toDate={today.AddDays(1):yyyy-MM-dd}",
            Json);
        throughput.Should().NotBeNull();
        var throughputRows = throughput!;
        throughputRows.Sum(r => r.IngestedCount).Should().Be(1);
    }

    private static ClaimFlow.Shared.Dtos.ClaimBundle Bundle() => new()
    {
        Id = "BND-SQL-E2E",
        PayerId = "PAYER-BCBS",
        MemberId = SqlServerFixture.MemberId,
        Patient = new ClaimFlow.Shared.Dtos.PatientResource
        {
            MemberId = SqlServerFixture.MemberId,
            GivenName = "Sql",
            FamilyName = "Tester",
            DateOfBirth = new DateOnly(1980, 1, 1),
            Ssn = "555-44-3333",
            Address = new ClaimFlow.Shared.Dtos.AddressResource
            {
                Line1 = "1 Test Way",
                City = "Austin",
                State = "TX",
                PostalCode = "78701",
            },
        },
        Coverage = new ClaimFlow.Shared.Dtos.CoverageResource
        {
            CoverageId = "COV-SQL-E2E",
            PayerId = "PAYER-BCBS",
            Status = ClaimFlow.Shared.CoverageStatus.Active,
            EffectiveDate = new DateOnly(2024, 1, 1),
            DeductibleRemainingCents = 0,
        },
        Claim = new ClaimFlow.Shared.Dtos.ClaimResource
        {
            ClaimId = SqlServerFixture.ClaimId,
            PayerId = "PAYER-BCBS",
            MemberId = SqlServerFixture.MemberId,
            ProviderNpi = "1234567893",
            PlaceOfService = "11",
            ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10),
            TotalChargeCents = 10_000,
            DiagnosisCodes = new[] { "J45.909" },
            Lines = new[]
            {
                new ClaimFlow.Shared.Dtos.ClaimLineItem
                {
                    LineNumber = 1,
                    CptCode = "99213",
                    Icd10Code = "J45.909",
                    PlaceOfService = "11",
                    ChargeCents = 10_000,
                    Units = 1,
                },
            },
        },
    };
}
