using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClaimFlow.Intake.Reporting;
using ClaimFlow.Security.Auth;
using ClaimFlow.Shared.Reporting;
using FluentAssertions;
using Xunit;

namespace ClaimFlow.Intake.Tests;

public sealed class ReportingTests : IClassFixture<IntakeApiFactory>
{
    private readonly IntakeApiFactory _factory;
    private readonly HttpClient _reportClient;

    public ReportingTests(IntakeApiFactory factory)
    {
        _factory = factory;
        _reportClient = factory.CreateAuthenticatedClient(ClaimFlowRoles.ReportingReader);
    }

    [Fact]
    public async Task Reports_return_denials_ar_rejects_and_throughput()
    {
        var admin = _factory.CreateAuthenticatedClient();
        var adjudicator = _factory.CreateAuthenticatedClient(ClaimFlowRoles.Adjudicator);

        var paid = TestData.WithSingleLine(TestData.Bundle("rep-paid"), 10_000);
        var denied = TestData.WithSingleLine(TestData.Bundle("rep-denied"), 12_000);
        var open = TestData.WithSingleLine(TestData.Bundle("rep-open"), 9_000);
        var rejected = MutateUnknownPayer(TestData.Bundle("rep-rejected"));

        foreach (var bundle in new[] { paid, denied, open, rejected })
        {
            await admin.PostAsJsonAsync("/api/claims", bundle, IntakeApiFactory.Json);
        }

        await UpdateStatusAsync(adjudicator, paid.Claim.ClaimId, new
        {
            status = "Paid",
            outcome = "PartiallyPaid",
            denialReasonCode = "PR1",
            allowedCents = 8_000,
            paidCents = 7_000,
            patientResponsibilityCents = 1_000,
            reason = "deductible",
        });

        await UpdateStatusAsync(adjudicator, denied.Claim.ClaimId, new
        {
            status = "Denied",
            outcome = "Denied",
            denialReasonCode = "CO50",
            allowedCents = 0,
            paidCents = 0,
            patientResponsibilityCents = 0,
            reason = "medical necessity",
        });

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = today.AddDays(-30);
        var to = today.AddDays(1);

        var denials = await _reportClient.GetFromJsonAsync<List<DenialRollupRow>>(
            $"/api/v1/reports/denials?fromDate={from:yyyy-MM-dd}&toDate={to:yyyy-MM-dd}",
            IntakeApiFactory.Json);
        denials.Should().NotBeNull();
        denials!.Should().Contain(r => r.DenialCode == "CO50" && r.ClaimCount == 1);
        denials.Should().Contain(r => r.DenialCode == "PR1" && r.ClaimCount == 1);
        denials.Should().OnlyContain(r => r.PercentOfDenials == 50m);
        denials.Should().OnlyContain(r => r.Rank == 1);

        var ar = await _reportClient.GetFromJsonAsync<List<DaysInArRow>>(
            $"/api/v1/reports/days-in-ar?asOfDate={today:yyyy-MM-dd}",
            IntakeApiFactory.Json);
        ar.Should().NotBeNull();
        var arRows = ar!;
        arRows.Should().NotBeEmpty();
        arRows.Sum(r => r.OpenClaimCount).Should().Be(2);
        arRows.Sum(r => r.OpenArCents).Should().Be(1_000 + 9_000);
        arRows.Should().OnlyContain(r => r.Bucket0To30Claims + r.Bucket31To60Claims +
            r.Bucket61To90Claims + r.Bucket90PlusClaims == r.OpenClaimCount);

        var rejects = await _reportClient.GetFromJsonAsync<List<ValidationRejectRow>>(
            $"/api/v1/reports/rejects?fromDate={from:yyyy-MM-dd}&toDate={to:yyyy-MM-dd}",
            IntakeApiFactory.Json);
        rejects.Should().NotBeNull();
        var rejectRows = rejects!;
        rejectRows.Should().ContainSingle(r => r.RuleCode == "PAY001" && r.RejectCount == 1);
        rejectRows.Single(r => r.RuleCode == "PAY001").PercentOfSubmissions.Should().Be(25m);

        var throughput = await _reportClient.GetFromJsonAsync<List<ThroughputRow>>(
            $"/api/v1/reports/throughput?fromDate={from:yyyy-MM-dd}&toDate={to:yyyy-MM-dd}",
            IntakeApiFactory.Json);
        throughput.Should().NotBeNull();
        var throughputRows = throughput!;
        throughputRows.Sum(r => r.IngestedCount).Should().Be(4);
        throughputRows.Sum(r => r.AdjudicatedCount).Should().Be(2);
    }

    [Fact]
    public async Task Report_endpoints_reject_anonymous_callers()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/v1/reports/denials");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void SqlServer_and_Linq_queries_share_the_same_DTO_contract()
    {
        var interfaceMethods = typeof(IReportingQuery).GetMethods();

        foreach (var method in interfaceMethods)
        {
            var sqlServer = typeof(SqlServerReportingQuery).GetMethod(method.Name);
            var linq = typeof(SqliteReportingQuery).GetMethod(method.Name);

            sqlServer.Should().NotBeNull();
            linq.Should().NotBeNull();
            sqlServer!.ReturnType.Should().Be(method.ReturnType);
            linq!.ReturnType.Should().Be(method.ReturnType);
            sqlServer.GetParameters().Select(p => p.ParameterType)
                .Should().Equal(method.GetParameters().Select(p => p.ParameterType));
            linq.GetParameters().Select(p => p.ParameterType)
                .Should().Equal(method.GetParameters().Select(p => p.ParameterType));
        }

        var sample = new DenialRollupRow("CO50", "Non-covered", 10, 100_000, 50m, 1);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(sample, IntakeApiFactory.Json));
        json.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            "denialCode",
            "description",
            "claimCount",
            "chargeCents",
            "percentOfDenials",
            "rank");
    }

    private static async Task UpdateStatusAsync(HttpClient client, string claimId, object body)
    {
        using var response = await client.PutAsJsonAsync(
            $"/api/claims/{claimId}/status", body, IntakeApiFactory.Json);
        response.EnsureSuccessStatusCode();
    }

    private static Shared.Dtos.ClaimBundle MutateUnknownPayer(Shared.Dtos.ClaimBundle bundle) => new()
    {
        Id = bundle.Id,
        PayerId = "PAYER-NOT-CONTRACTED",
        MemberId = bundle.MemberId,
        Patient = bundle.Patient,
        Coverage = bundle.Coverage,
        Claim = bundle.Claim,
    };
}
