using System.Net;
using System.Net.Http.Json;
using ClaimFlow.Adjudication.Services;
using ClaimFlow.Data;
using ClaimFlow.Intake.Contracts;
using ClaimFlow.Messaging;
using ClaimFlow.Shared;
using ClaimFlow.Shared.Messaging;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaimFlow.Integration.Tests;

public sealed class EndToEndTests : IClassFixture<EndToEndFixture>
{
    private static readonly SyntheticCoverageStore CoverageStore = new();
    private readonly EndToEndFixture _fixture;

    public EndToEndTests(EndToEndFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Claim_flows_received_validated_adjudicated_paid_and_duplicate_event_is_ignored()
    {
        var memberId = CoverageStore.FindActiveMembersWithoutDeductible(1).Single();
        var bundle = EndToEndData.Bundle("paid", memberId, chargeCents: 12_345);

        var created = await _fixture.IntakeClient.PostAsJsonAsync("/api/claims", bundle, IntegrationJson.Options);
        var createdBody = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, createdBody);

        var detail = await WaitForFinalStatusAsync(bundle.Claim.ClaimId);
        detail.Status.Should().Be(ClaimStatus.Paid);
        detail.Outcome.Should().Be(AdjudicationOutcome.Paid);
        detail.DenialReasonCode.Should().BeNull();
        detail.PaidCents.Should().BeGreaterThan(0);
        detail.PatientResponsibilityCents.Should().Be(0);
        detail.StatusHistory.Select(h => h.Status).Should().Equal(
            ClaimStatus.Received,
            ClaimStatus.Validated,
            ClaimStatus.Adjudicated,
            ClaimStatus.Paid);

        using var scope = _fixture.Intake.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimFlowDbContext>();
        var outbox = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(o => o.Payload.Contains(bundle.Claim.ClaimId));
        outbox.ProcessedAt.Should().NotBeNull();

        var payload = ClaimEventJson.Deserialize<ClaimSubmittedPayload>(outbox.Payload)!;
        await EndToEndFixture.WaitUntilAsync(async () =>
        {
            using var waitScope = _fixture.Intake.Services.CreateScope();
            var waitDb = waitScope.ServiceProvider.GetRequiredService<ClaimFlowDbContext>();
            return await waitDb.ProcessedEvents.AsNoTracking().AnyAsync(e => e.EventId == payload.EventId);
        }, TimeSpan.FromSeconds(10));
        var processedCount = await db.ProcessedEvents.AsNoTracking().CountAsync(e => e.EventId == payload.EventId);
        processedCount.Should().Be(1);

        var bus = _fixture.Intake.Services.GetRequiredService<IClaimEventBus>();
        var envelope = new ClaimEventEnvelope(
            payload.EventId,
            ClaimEventTypes.ClaimSubmitted,
            payload.CorrelationId,
            payload.OccurredAt,
            System.Text.Json.JsonSerializer.SerializeToElement(payload, ClaimEventJson.Options));
        await bus.PublishAsync(envelope);

        await Task.Delay(750);

        var afterDuplicate = await _fixture.IntakeClient.GetFromJsonAsync<ClaimDetailDto>(
            $"/api/claims/{bundle.Claim.ClaimId}", IntegrationJson.Options);
        afterDuplicate!.Status.Should().Be(ClaimStatus.Paid);
        afterDuplicate.StatusHistory.Should().HaveCount(4);
    }

    [Fact]
    public async Task Termed_coverage_is_denied_by_the_consumer_with_CO27()
    {
        var memberId = FindMember(coverage => coverage.Status == CoverageStatus.Termed);
        var bundle = EndToEndData.Bundle("termed", memberId, chargeCents: 13_456);

        var created = await _fixture.IntakeClient.PostAsJsonAsync("/api/claims", bundle, IntegrationJson.Options);
        var createdBody = await created.Content.ReadAsStringAsync();
        created.StatusCode.Should().Be(HttpStatusCode.Created, createdBody);

        var detail = await WaitForFinalStatusAsync(bundle.Claim.ClaimId);
        detail.Status.Should().Be(ClaimStatus.Denied);
        detail.Outcome.Should().Be(AdjudicationOutcome.Denied);
        detail.DenialReasonCode.Should().Be(DenialReasonCode.CO27);
        detail.StatusHistory.Select(h => h.Status).Should().Equal(
            ClaimStatus.Received,
            ClaimStatus.Validated,
            ClaimStatus.Adjudicated,
            ClaimStatus.Denied);
    }

    [Fact]
    public async Task Ingest_batch_drains_to_zero_claims_stuck_in_validated()
    {
        var active = CoverageStore.FindActiveMembersWithoutDeductible(4).ToArray();
        var termed = FindMemberAndNext(coverage => coverage.Status == CoverageStatus.Termed, 4).ToArray();
        var bundles = new List<Shared.Dtos.ClaimBundle>();

        for (var i = 0; i < 6; i++)
        {
            bundles.Add(EndToEndData.Bundle($"drain{i}", active[i % active.Length],
                cptCode: i % 3 == 0 ? "93000" : "99213",
                chargeCents: 10_000 + (i * 1_111)));
        }

        for (var i = 0; i < 4; i++)
        {
            bundles.Add(EndToEndData.Bundle($"drain-denied{i}", termed[i]));
        }

        foreach (var bundle in bundles)
        {
            var response = await _fixture.IntakeClient.PostAsJsonAsync("/api/claims", bundle, IntegrationJson.Options);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        await EndToEndFixture.WaitUntilAsync(async () =>
        {
            var validated = await _fixture.IntakeClient.GetFromJsonAsync<PagedResult<ClaimSummaryDto>>(
                "/api/claims?status=Validated&page=1&pageSize=100", IntegrationJson.Options);
            return validated!.TotalCount == 0;
        }, TimeSpan.FromSeconds(30));

        foreach (var bundle in bundles)
        {
            var detail = await _fixture.IntakeClient.GetFromJsonAsync<ClaimDetailDto>(
                $"/api/claims/{bundle.Claim.ClaimId}", IntegrationJson.Options);
            detail!.Status.Should().BeOneOf(ClaimStatus.Paid, ClaimStatus.Denied);
        }
    }

    private async Task<ClaimDetailDto> WaitForFinalStatusAsync(string claimId)
    {
        ClaimDetailDto? detail = null;
        await EndToEndFixture.WaitUntilAsync(async () =>
        {
            detail = await _fixture.IntakeClient.GetFromJsonAsync<ClaimDetailDto>(
                $"/api/claims/{claimId}", IntegrationJson.Options);
            return detail is { Status: ClaimStatus.Paid or ClaimStatus.Denied };
        }, TimeSpan.FromSeconds(30));

        return detail!;
    }

    private static string FindMember(Func<SyntheticCoverage, bool> predicate)
    {
        for (var i = 0; i < 100_000; i++)
        {
            var candidate = $"MBR-{i:D6}";
            if (predicate(CoverageStore.Get(candidate)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No matching synthetic member found.");
    }

    private static IEnumerable<string> FindMemberAndNext(Func<SyntheticCoverage, bool> predicate, int count)
    {
        var found = 0;
        for (var i = 0; i < 100_000 && found < count; i++)
        {
            var candidate = $"MBR-{i:D6}";
            if (!predicate(CoverageStore.Get(candidate)))
            {
                continue;
            }

            found++;
            yield return candidate;
        }
    }
}
