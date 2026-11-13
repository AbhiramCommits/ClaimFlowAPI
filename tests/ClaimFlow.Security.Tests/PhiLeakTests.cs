using System.Net;
using System.Net.Http.Json;
using ClaimFlow.Security.Auth;
using ClaimFlow.Security.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Context;
using Xunit;

namespace ClaimFlow.Security.Tests;

public sealed class PhiLeakTests : IClassFixture<SecurityApiFixture>
{
    private readonly SecurityApiFixture _fixture;

    public PhiLeakTests(SecurityApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Full_ingest_and_exception_paths_never_log_raw_phi()
    {
        var sink = _fixture.Intake.Services.GetRequiredService<InMemorySink>();
        sink.Clear();

        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:Console"] = "false",
                ["Serilog:InMemory"] = "true",
            })
            .Build();
        using var probeServices = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(sink)
            .BuildServiceProvider();
        var logger = new Serilog.LoggerConfiguration()
            .ConfigureClaimFlow(configuration, probeServices, "intake")
            .CreateLogger();

        var client = _fixture.CreateClient("intake", ClaimFlowRoles.ClaimsWriter);
        var bundle = SecurityTestData.PhiBundle();

        var response = await client.PostAsJsonAsync("/api/claims", bundle, SecurityJson.Options);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        logger.Information("PHI destructuring probe {@Bundle}", bundle);
        logger.Error(
            new InvalidOperationException("simulated downstream failure"),
            "PHI exception probe {@Bundle}",
            bundle);
        using (LogContext.PushProperty("MemberId", bundle.MemberId))
        using (LogContext.PushProperty("DateOfBirth", "1971-12-31"))
        {
            logger.Warning("PHI log-context probe");
        }

        var allText = sink.AllText;

        allText.Should().NotContain(SecurityTestData.LeakMemberId);
        allText.Should().NotContain(SecurityTestData.LeakGivenName);
        allText.Should().NotContain(SecurityTestData.LeakFamilyName);
        allText.Should().NotContain(SecurityTestData.LeakSsn);
        allText.Should().NotContain("321549876");
        allText.Should().NotContain(SecurityTestData.LeakDateOfBirth);
        allText.Should().NotContain(SecurityTestData.LeakAddress);
        allText.Should().Contain("phi_guard");
        allText.Should().Contain("****7654", allText);
    }

    [Fact]
    public void Redactor_keeps_only_safe_fragments()
    {
        PhiRedactor.Redact("MemberId", "MBR-LEAK-987654").Should().Be("****7654");
        PhiRedactor.Redact("Ssn", "321-54-9876").Should().Be("***-**-9876");
        PhiRedactor.Redact("GivenName", "Zeldaunique").Should().Be("Z***");
        PhiRedactor.Redact("FamilyName", "Quarantine").Should().Be("Q***");
        PhiRedactor.Redact("DateOfBirth", new DateOnly(1971, 12, 31)).Should().StartWith("DOB#");
        PhiRedactor.Redact("Address", "999 Secret Lane").Should().Contain("REDACTED");
        PhiRedactor.HashBody("{\"ssn\":\"321-54-9876\"}").Should().StartWith("sha256:");
        PhiRedactor.HashBody("{\"ssn\":\"321-54-9876\"}").Should().NotContain("321-54-9876");
    }

    [Theory]
    [InlineData("MemberId")]
    [InlineData("memberId")]
    [InlineData("SSN")]
    [InlineData("DateOfBirth")]
    [InlineData("GivenName")]
    [InlineData("FamilyName")]
    [InlineData("Address")]
    [InlineData("RequestBody")]
    public void Declared_phi_fields_are_sensitive(string field)
    {
        PhiRedactor.IsSensitiveName(field).Should().BeTrue();
        PhiRedactor.SensitiveFieldNames.Should().Contain(
            PhiRedactor.SensitiveFieldNames.First(name => name.Equals(field, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Serilog_pipeline_masks_log_context_properties_and_exceptions()
    {
        var sink = new InMemorySink();
        var logger = new Serilog.LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.With<PhiMaskingEnricher>()
            .Destructure.With<PhiDestructuringPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        using (LogContext.PushProperty("MemberId", "MBR-LEAK-112233"))
        using (LogContext.PushProperty("Ssn", "321-54-9876"))
        {
            logger.Information("context probe");
        }

        var text = sink.AllText;
        text.Should().NotContain("MBR-LEAK-112233");
        text.Should().NotContain("321-54-9876");
        text.Should().Contain("****2233");
        text.Should().Contain("***-**-9876");
    }
}
