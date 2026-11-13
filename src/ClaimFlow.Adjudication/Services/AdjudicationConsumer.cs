using System.Text.Json;
using ClaimFlow.Data;
using ClaimFlow.Data.Entities;
using ClaimFlow.Messaging;
using ClaimFlow.Shared;
using ClaimFlow.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Adjudication.Services;

/// <summary>
/// Consumes claim.submitted, checks eligibility, adjudicates, reports status to Intake over HTTP
/// and publishes claim.adjudicated. Idempotent per (claim id, event id) via the ProcessedEvents inbox.
/// </summary>
public sealed class AdjudicationConsumer : BackgroundService
{
    private readonly IClaimEventBus _bus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEligibilityService _eligibility;
    private readonly AdjudicationEngine _engine;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AdjudicationConsumer> _logger;

    public AdjudicationConsumer(
        IClaimEventBus bus,
        IServiceScopeFactory scopeFactory,
        IEligibilityService eligibility,
        AdjudicationEngine engine,
        TimeProvider timeProvider,
        ILogger<AdjudicationConsumer> logger)
    {
        _bus = bus;
        _scopeFactory = scopeFactory;
        _eligibility = eligibility;
        _engine = engine;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _bus.SubscribeAsync(ClaimEventTypes.ClaimSubmitted, HandleAsync, stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to subscribe to claim.submitted; retrying in 2 seconds.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task HandleAsync(ClaimEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var payload = ClaimEventJson.Deserialize<ClaimSubmittedPayload>(envelope.Data);
        if (payload is null)
        {
            throw new InvalidOperationException($"Claim event {envelope.EventId} has no payload.");
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClaimFlowDbContext>();

        if (await db.ProcessedEvents.AnyAsync(e => e.EventId == envelope.EventId, cancellationToken))
        {
            _logger.LogInformation(
                "Duplicate event ignored. EventId={EventId} ClaimId={ClaimId}",
                envelope.EventId,
                payload.ClaimId);
            return;
        }

        var eligibility = _eligibility.Check(new EligibilityRequest(
            payload.MemberId,
            payload.PayerId,
            payload.ServiceDate));

        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);
        var result = _engine.Adjudicate(
            new AdjudicationRequest(
                payload.ClaimId,
                payload.PayerId,
                payload.MemberId,
                payload.ServiceDate,
                payload.Lines
                    .Select(l => new AdjudicationLineRequest(l.LineNumber, l.CptCode, l.Icd10Code, l.ChargeCents))
                    .ToArray(),
                payload.Lines.Select(l => l.Icd10Code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()),
            eligibility,
            today);

        var finalStatus = result.Outcome == AdjudicationOutcome.Denied ? ClaimStatus.Denied : ClaimStatus.Paid;
        var statusClient = scope.ServiceProvider.GetRequiredService<IntakeStatusClient>();
        await statusClient.UpdateStatusAsync(
            payload.ClaimId,
            new ClaimStatusUpdatePayload(
                finalStatus,
                result.Outcome,
                result.DenialReasonCode,
                result.AllowedCents,
                result.PaidCents,
                result.PatientResponsibilityCents,
                $"Adjudicated: {result.Outcome} {result.DenialReasonCode}",
                envelope.EventId,
                "adjudication"),
            cancellationToken);

        db.ProcessedEvents.Add(new ProcessedEvent
        {
            EventId = envelope.EventId,
            ClaimId = payload.ClaimId,
            Consumer = "adjudication",
            ProcessedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _logger.LogInformation(
                "Concurrent duplicate event detected while recording inbox entry. EventId={EventId}",
                envelope.EventId);
            return;
        }

        var adjudicatedAt = _timeProvider.GetUtcNow();
        var adjudicated = new ClaimAdjudicatedPayload(
            Guid.NewGuid(),
            payload.ClaimId,
            payload.PayerId,
            payload.MemberId,
            result.Outcome.ToString(),
            result.DenialReasonCode is { } code ? DenialReasons.Code(code) : null,
            result.AllowedCents,
            result.PaidCents,
            result.PatientResponsibilityCents,
            payload.CorrelationId,
            adjudicatedAt);

        await _bus.PublishAsync(
            new ClaimEventEnvelope(
                adjudicated.EventId,
                ClaimEventTypes.ClaimAdjudicated,
                payload.CorrelationId,
                adjudicatedAt,
                JsonSerializer.SerializeToElement(adjudicated, ClaimEventJson.Options)),
            cancellationToken);

        _logger.LogInformation(
            "Claim adjudicated. ClaimId={ClaimId} Outcome={Outcome} Denial={DenialReasonCode} AllowedCents={AllowedCents} PaidCents={PaidCents}",
            payload.ClaimId,
            result.Outcome,
            result.DenialReasonCode,
            result.AllowedCents,
            result.PaidCents);
    }
}
