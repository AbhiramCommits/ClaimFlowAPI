using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClaimFlow.Intake.Contracts;
using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ClaimFlow.Intake.Tests;

public sealed class IntakeApiTests : IClassFixture<IntakeApiFactory>
{
    private readonly IntakeApiFactory _factory;
    private readonly HttpClient _client;

    public IntakeApiTests(IntakeApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Happy_path_persists_claim_lines_history_and_outbox()
    {
        var bundle = TestData.Bundle("happy");

        var response = await _client.PostAsJsonAsync("/api/claims", bundle, IntakeApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var detail = await response.Content.ReadFromJsonAsync<ClaimDetailDto>(IntakeApiFactory.Json);
        detail.Should().NotBeNull();
        detail!.Status.Should().Be(ClaimStatus.Validated);
        detail.Lines.Should().HaveCount(2);
        detail.StatusHistory.Select(h => h.Status).Should().Equal(ClaimStatus.Received, ClaimStatus.Validated);
        detail.TotalChargeCents.Should().Be(22_500);

        var fetched = await _client.GetFromJsonAsync<ClaimDetailDto>($"/api/claims/{bundle.Claim.ClaimId}", IntakeApiFactory.Json);
        fetched!.ClaimId.Should().Be(bundle.Claim.ClaimId);
        fetched.Lines.Sum(l => l.ChargeCents).Should().Be(fetched.TotalChargeCents);

        using var db = _factory.CreateDbContext();
        var outbox = await db.OutboxMessages.SingleOrDefaultAsync(o => o.CorrelationId.Length > 0 &&
            o.Payload.Contains(bundle.Claim.ClaimId) && o.ProcessedAt == null);
        outbox.Should().NotBeNull();
        outbox!.EventType.Should().Be("claim.submitted");
    }

    [Theory]
    [InlineData("missing-member", "MBR001")]
    [InlineData("unknown-payer", "PAY001")]
    [InlineData("stale-date", "SVC002")]
    [InlineData("invalid-npi", "NPI001")]
    [InlineData("invalid-cpt", "CPT001")]
    [InlineData("line-mismatch", "CHG002")]
    [InlineData("duplicate-line", "DUP001")]
    [InlineData("missing-pos", "POS001")]
    public async Task Validation_rejection_is_persisted_with_rule_code(string scenario, string ruleCode)
    {
        var bundle = Mutate(TestData.Bundle(scenario), scenario);

        var response = await _client.PostAsJsonAsync("/api/claims", bundle, IntakeApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("ruleCodes").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain(ruleCode);
        var claimId = problem.RootElement.GetProperty("claimId").GetString();
        claimId.Should().Be(bundle.Claim.ClaimId);

        using var db = _factory.CreateDbContext();
        var persisted = await db.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == claimId);
        persisted.Status.Should().Be(ClaimStatus.Rejected);
        persisted.ValidationRuleCodes.Should().Contain(ruleCode);
    }

    [Fact]
    public async Task Duplicate_claim_returns_conflict_with_CO18()
    {
        var bundle = TestData.Bundle("dupe");

        var first = await _client.PostAsJsonAsync("/api/claims", bundle, IntakeApiFactory.Json);
        var second = await _client.PostAsJsonAsync("/api/claims", bundle, IntakeApiFactory.Json);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var problem = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("ruleCodes").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("CO18");
        problem.RootElement.GetProperty("title").GetString().Should().Be("Duplicate claim");
    }

    [Fact]
    public async Task Ndjson_batch_returns_per_record_accepted_and_rejected_results()
    {
        var validOne = TestData.Bundle("batch1");
        var validTwo = TestData.Bundle("batch2");
        var invalid = Mutate(TestData.Bundle("batch3"), "invalid-cpt");
        var ndjson = new StringBuilder()
            .AppendLine(JsonSerializer.Serialize(validOne, IntakeApiFactory.Json))
            .AppendLine(JsonSerializer.Serialize(invalid, IntakeApiFactory.Json))
            .AppendLine(JsonSerializer.Serialize(validTwo, IntakeApiFactory.Json))
            .ToString();

        using var content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson");
        var response = await _client.PostAsync("/api/claims/batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var batch = await response.Content.ReadFromJsonAsync<BatchIngestResponse>(IntakeApiFactory.Json);
        batch.Should().NotBeNull();
        batch!.Total.Should().Be(3);
        batch.Accepted.Should().Be(2);
        batch.Rejected.Should().Be(1);
        batch.Duplicates.Should().Be(0);
        batch.Results.Single(r => r.Status == "Rejected").RuleCodes.Should().Contain("CPT001");
    }

    [Fact]
    public async Task Json_array_batch_is_supported()
    {
        var bundles = new[] { TestData.Bundle("array1"), TestData.Bundle("array2") };

        var response = await _client.PostAsJsonAsync("/api/claims/batch", bundles, IntakeApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var batch = await response.Content.ReadFromJsonAsync<BatchIngestResponse>(IntakeApiFactory.Json);
        batch!.Total.Should().Be(2);
        batch.Accepted.Should().Be(2);
    }

    [Fact]
    public async Task Status_update_records_adjudicated_and_final_history_rows()
    {
        var bundle = TestData.Bundle("status");
        var created = await _client.PostAsJsonAsync("/api/claims", bundle, IntakeApiFactory.Json);
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var request = new ClamStatusUpdate
        {
            Status = "Paid",
            Outcome = "PartiallyPaid",
            DenialReasonCode = "PR1",
            AllowedCents = 10_000,
            PaidCents = 8_000,
            PatientResponsibilityCents = 2_000,
            Reason = "Deductible applied",
            EventId = Guid.NewGuid(),
        };

        var response = await _client.PutAsJsonAsync($"/api/claims/{bundle.Claim.ClaimId}/status", request, IntakeApiFactory.Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<ClaimDetailDto>(IntakeApiFactory.Json);
        detail!.Status.Should().Be(ClaimStatus.Paid);
        detail.Outcome.Should().Be(AdjudicationOutcome.PartiallyPaid);
        detail.DenialReasonCode.Should().Be(DenialReasonCode.PR1);
        detail.StatusHistory.Select(h => h.Status).Should().Equal(
            ClaimStatus.Received, ClaimStatus.Validated, ClaimStatus.Adjudicated, ClaimStatus.Paid);
        detail.Lines.Sum(l => l.AllowedCents).Should().Be(10_000);
        detail.Lines.Sum(l => l.PaidCents).Should().Be(8_000);

        var replay = await _client.PutAsJsonAsync($"/api/claims/{bundle.Claim.ClaimId}/status", request, IntakeApiFactory.Json);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        var replayed = await replay.Content.ReadFromJsonAsync<ClaimDetailDto>(IntakeApiFactory.Json);
        replayed!.StatusHistory.Should().HaveCount(4);
    }

    private sealed class ClamStatusUpdate
    {
        public string Status { get; init; } = string.Empty;

        public string? Outcome { get; init; }

        public string? DenialReasonCode { get; init; }

        public int AllowedCents { get; init; }

        public int PaidCents { get; init; }

        public int PatientResponsibilityCents { get; init; }

        public string? Reason { get; init; }

        public Guid? EventId { get; init; }
    }

    private static ClaimBundle Mutate(ClaimBundle bundle, string scenario) => scenario switch
    {
        "missing-member" => new ClaimBundle
        {
            Id = bundle.Id,
            PayerId = bundle.PayerId,
            MemberId = string.Empty,
            Patient = new PatientResource
            {
                MemberId = string.Empty,
                GivenName = bundle.Patient.GivenName,
                FamilyName = bundle.Patient.FamilyName,
                DateOfBirth = bundle.Patient.DateOfBirth,
                Ssn = bundle.Patient.Ssn,
                Address = bundle.Patient.Address,
            },
            Coverage = bundle.Coverage,
            Claim = bundle.Claim,
        },
        "unknown-payer" => WithPayer(bundle, "PAYER-NOPE"),
        "stale-date" => WithClaim(bundle, c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-400),
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        }),
        "invalid-npi" => WithClaim(bundle, c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = "1234567890",
            PlaceOfService = c.PlaceOfService,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        }),
        "invalid-cpt" => WithClaim(bundle, c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = new[]
            {
                new ClaimLineItem { LineNumber = 1, CptCode = "ABCDE", Icd10Code = "J45.909", PlaceOfService = "11", ChargeCents = c.TotalChargeCents, Units = 1 },
            },
        }),
        "line-mismatch" => WithClaim(bundle, c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = c.TotalChargeCents + 1,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        }),
        "duplicate-line" => WithClaim(bundle, c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = 30_000,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = new[]
            {
                new ClaimLineItem { LineNumber = 1, CptCode = "99213", Icd10Code = "J45.909", PlaceOfService = "11", ChargeCents = 15_000, Units = 1 },
                new ClaimLineItem { LineNumber = 2, CptCode = "99213", Icd10Code = "J45.909", PlaceOfService = "11", ChargeCents = 15_000, Units = 1 },
            },
        }),
        "missing-pos" => WithClaim(bundle, c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = string.Empty,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines.Select(l => new ClaimLineItem
            {
                LineNumber = l.LineNumber,
                CptCode = l.CptCode,
                Icd10Code = l.Icd10Code,
                PlaceOfService = string.Empty,
                ChargeCents = l.ChargeCents,
                Units = l.Units,
            }).ToArray(),
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
    };

    private static ClaimBundle WithPayer(ClaimBundle bundle, string payerId) => new()
    {
        Id = bundle.Id,
        PayerId = payerId,
        MemberId = bundle.MemberId,
        Patient = bundle.Patient,
        Coverage = bundle.Coverage,
        Claim = bundle.Claim,
    };

    private static ClaimBundle WithClaim(ClaimBundle bundle, Func<ClaimResource, ClaimResource> mutate) => new()
    {
        Id = bundle.Id,
        PayerId = bundle.PayerId,
        MemberId = bundle.MemberId,
        Patient = bundle.Patient,
        Coverage = bundle.Coverage,
        Claim = mutate(bundle.Claim),
    };
}
