using System.Collections.Concurrent;
using ClaimFlow.Data;
using ClaimFlow.Data.Entities;
using ClaimFlow.Intake.Contracts;
using ClaimFlow.Shared;
using ClaimFlow.Shared.Domain;
using ClaimFlow.Shared.Messaging;
using ClaimFlow.Shared.Normalization;
using ClaimFlow.Shared.Validation;
using ClaimFlow.Shared.Dtos;
using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Intake.Services;

public enum IngestKind
{
    Accepted,
    Rejected,
    Duplicate,
}

public sealed record IngestOutcome(
    IngestKind Kind,
    Claim? Claim,
    IReadOnlyList<ValidationIssue> Errors);

/// <summary>
/// Validates, normalizes and persists a claim with its outbox event in one transaction.
/// Rejected claims are persisted too so the reporting layer can count every submission.
/// </summary>
public sealed class ClaimIngestService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> MemberGates =
        new(StringComparer.Ordinal);

    private readonly ClaimFlowDbContext _db;
    private readonly ILogger<ClaimIngestService> _logger;

    public ClaimIngestService(ClaimFlowDbContext db, ILogger<ClaimIngestService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IngestOutcome> IngestAsync(
        ClaimBundle bundle,
        string correlationId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var validation = ClaimValidator.Validate(bundle, today);
        if (!validation.IsValid)
        {
            var memberKey = (bundle.MemberId ?? string.Empty).Trim().ToUpperInvariant();
            var gate = memberKey.Length > 0 ? GetMemberGate(memberKey) : null;
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken);
            }

            try
            {
                var rejected = await PersistRejectedAsync(bundle, validation, correlationId, today, cancellationToken);
                _logger.LogWarning(
                    "Claim rejected by validation. ClaimId={ClaimId} RuleCodes={RuleCodes}",
                    rejected.ClaimId,
                    rejected.ValidationRuleCodes);
                return new IngestOutcome(IngestKind.Rejected, rejected, validation.Errors);
            }
            finally
            {
                gate?.Release();
            }
        }

        var normalized = ClaimNormalizer.Normalize(bundle, correlationId);

        // A member may appear on several claims ingested concurrently. Serialize the
        // duplicate check + patient upsert per member so two requests cannot race to insert
        // the same unique patient row (the natural-key index still protects claim duplicates).
        var memberGate = GetMemberGate(normalized.Claim.MemberId);
        await memberGate.WaitAsync(cancellationToken);
        try
        {
            var naturalKeyExists = await _db.Claims.AnyAsync(
                c => c.PayerId == normalized.Claim.PayerId &&
                     c.MemberId == normalized.Claim.MemberId &&
                     c.ServiceDate == normalized.Claim.ServiceDate &&
                     c.TotalChargeCents == normalized.Claim.TotalChargeCents &&
                     c.Status != ClaimStatus.Rejected,
                cancellationToken);

            if (naturalKeyExists)
            {
                _logger.LogWarning("Duplicate claim detected. ClaimId={ClaimId}", normalized.Claim.ClaimId);
                return new IngestOutcome(IngestKind.Duplicate, null, Array.Empty<ValidationIssue>());
            }

            var patient = await UpsertPatientAsync(normalized.Patient, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var claim = BuildClaim(normalized, patient, now, correlationId);
            _db.Claims.Add(claim);

            claim.StatusHistory.Add(new ClaimStatusHistory
            {
                ClaimId = claim.Id,
                Status = ClaimStatus.Received,
                Sequence = 1,
                ChangedAt = now,
                Reason = "Claim received from provider",
                Actor = "intake",
                CorrelationId = correlationId,
            });

            claim.StatusHistory.Add(new ClaimStatusHistory
            {
                ClaimId = claim.Id,
                Status = ClaimStatus.Validated,
                Sequence = 2,
                ChangedAt = now,
                Reason = "Validation and normalization passed",
                Actor = "intake",
                CorrelationId = correlationId,
            });

            claim.Status = ClaimStatus.Validated;

            var payload = new ClaimSubmittedPayload(
                EventId: Guid.NewGuid(),
                ClaimId: claim.ClaimId,
                PayerId: claim.PayerId,
                MemberId: claim.MemberId,
                ProviderNpi: claim.ProviderNpi,
                PlaceOfService: claim.PlaceOfService,
                ServiceDate: claim.ServiceDate,
                TotalChargeCents: claim.TotalChargeCents,
                Lines: normalized.Claim.Lines.Select(l => new ClaimLinePayload(
                    l.LineNumber, l.CptCode, l.Icd10Code, l.PlaceOfService, l.ChargeCents, l.Units)).ToArray(),
                CorrelationId: correlationId,
                OccurredAt: now);

            _db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = ClaimEventTypes.ClaimSubmitted,
                Payload = ClaimEventJson.Serialize(payload),
                CorrelationId = correlationId,
                OccurredAt = now,
            });

            try
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (IsNaturalKeyConflict(ex))
            {
                _logger.LogWarning(ex, "Claim insert conflicted with an existing claim. ClaimId={ClaimId}", claim.ClaimId);
                return new IngestOutcome(IngestKind.Duplicate, null, Array.Empty<ValidationIssue>());
            }

            _logger.LogInformation(
                "Claim accepted. ClaimId={ClaimId} Payer={PayerId} ChargeCents={ChargeCents}",
                claim.ClaimId,
                claim.PayerId,
                claim.TotalChargeCents);

            return new IngestOutcome(IngestKind.Accepted, claim, Array.Empty<ValidationIssue>());
        }
        finally
        {
            memberGate.Release();
        }
    }

    private async Task<Claim> PersistRejectedAsync(
        ClaimBundle bundle,
        ValidationResult validation,
        string correlationId,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var memberId = (bundle.MemberId ?? string.Empty).Trim().ToUpperInvariant();
        var claimId = !string.IsNullOrWhiteSpace(bundle.Claim.ClaimId)
            ? bundle.Claim.ClaimId.Trim()
            : !string.IsNullOrWhiteSpace(bundle.Id) ? bundle.Id.Trim() : $"REJ-{Guid.NewGuid():N}";
        claimId = Truncate(claimId, 64);

        Guid? patientId = null;
        if (memberId.Length > 0)
        {
            var normalizedPatient = ClaimNormalizer.NormalizePatient(bundle.Patient, memberId);
            var patient = await UpsertPatientAsync(normalizedPatient, cancellationToken);
            patientId = patient.Id;
        }

        var claim = new Claim
        {
            ClaimId = claimId,
            PayerId = Truncate((bundle.PayerId ?? string.Empty).Trim().ToUpperInvariant(), 32),
            MemberId = Truncate(memberId, 64),
            PatientId = patientId,
            ProviderNpi = Truncate((bundle.Claim.ProviderNpi ?? string.Empty).Trim(), 10),
            PlaceOfService = Truncate((bundle.Claim.PlaceOfService ?? string.Empty).Trim().ToUpperInvariant(), 2),
            ServiceDate = bundle.Claim.ServiceDate == default ? today : bundle.Claim.ServiceDate,
            TotalChargeCents = bundle.Claim.TotalChargeCents,
            Status = ClaimStatus.Rejected,
            ValidationRuleCodes = string.Join(',', validation.RuleCodes),
            CorrelationId = correlationId,
            ReceivedAt = now,
            ReceivedAtUnixMs = now.ToUnixTimeMilliseconds(),
            LastStatusChangeAt = now,
        };

        foreach (var line in bundle.Claim.Lines ?? Array.Empty<ClaimLineItem>())
        {
            claim.Lines.Add(new ClaimLine
            {
                ClaimId = claim.Id,
                LineNumber = line.LineNumber,
                CptCode = Truncate((line.CptCode ?? string.Empty).Trim().ToUpperInvariant(), 10),
                Icd10Code = Truncate((line.Icd10Code ?? string.Empty).Trim().ToUpperInvariant(), 10),
                PlaceOfService = Truncate((line.PlaceOfService ?? string.Empty).Trim().ToUpperInvariant(), 2),
                ChargeCents = line.ChargeCents,
                Units = line.Units,
            });
        }

        claim.StatusHistory.Add(new ClaimStatusHistory
        {
            ClaimId = claim.Id,
            Status = ClaimStatus.Received,
            Sequence = 1,
            ChangedAt = now,
            Reason = "Claim received from provider",
            Actor = "intake",
            CorrelationId = correlationId,
        });

        claim.StatusHistory.Add(new ClaimStatusHistory
        {
            ClaimId = claim.Id,
            Status = ClaimStatus.Rejected,
            Sequence = 2,
            ChangedAt = now,
            Reason = $"Validation failed: {claim.ValidationRuleCodes}",
            Actor = "intake",
            CorrelationId = correlationId,
        });

        _db.Claims.Add(claim);
        await _db.SaveChangesAsync(cancellationToken);
        return claim;
    }

    private async Task<Patient> UpsertPatientAsync(NormalizedPatient normalized, CancellationToken cancellationToken)
    {
        var existing = await _db.Patients.FirstOrDefaultAsync(p => p.MemberId == normalized.MemberId, cancellationToken);
        if (existing is null)
        {
            existing = new Patient { MemberId = normalized.MemberId };
            _db.Patients.Add(existing);
        }

        existing.GivenName = normalized.GivenName;
        existing.FamilyName = normalized.FamilyName;
        existing.DateOfBirth = normalized.DateOfBirth;
        existing.SsnLast4 = normalized.SsnLast4;
        existing.SsnHash = normalized.SsnHash;
        existing.AddressLine1 = normalized.Address.Line1;
        existing.City = normalized.Address.City;
        existing.State = normalized.Address.State;
        existing.PostalCode = normalized.Address.PostalCode;
        return existing;
    }

    private static Claim BuildClaim(
        NormalizationResult normalized,
        Patient patient,
        DateTimeOffset now,
        string correlationId)
    {        var claim = new Claim
        {
            ClaimId = Truncate(normalized.Claim.ClaimId, 64),
            PayerId = normalized.Claim.PayerId,
            MemberId = normalized.Claim.MemberId,
            PatientId = patient.Id,
            ProviderNpi = normalized.Claim.ProviderNpi,
            PlaceOfService = normalized.Claim.PlaceOfService,
            ServiceDate = normalized.Claim.ServiceDate,
            TotalChargeCents = normalized.Claim.TotalChargeCents,
            Status = ClaimStatus.Validated,
            CorrelationId = correlationId,
            ReceivedAt = now,
            ReceivedAtUnixMs = now.ToUnixTimeMilliseconds(),
            LastStatusChangeAt = now,
        };

        foreach (var line in normalized.Claim.Lines)
        {
            claim.Lines.Add(new ClaimLine
            {
                ClaimId = claim.Id,
                LineNumber = line.LineNumber,
                CptCode = line.CptCode,
                Icd10Code = line.Icd10Code,
                PlaceOfService = line.PlaceOfService,
                ChargeCents = line.ChargeCents,
                Units = line.Units,
            });
        }

        return claim;
    }

    private static SemaphoreSlim GetMemberGate(string memberId) =>
        MemberGates.GetOrAdd(memberId, _ => new SemaphoreSlim(1, 1));

    private static bool IsNaturalKeyConflict(DbUpdateException exception)
    {
        var message = exception.InnerException?.Message ?? exception.Message;
        return message.Contains("UX_Claims_NaturalKey", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("UNIQUE constraint failed: Claims.", StringComparison.OrdinalIgnoreCase);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}