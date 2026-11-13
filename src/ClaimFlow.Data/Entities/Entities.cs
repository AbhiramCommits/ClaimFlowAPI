using ClaimFlow.Shared;

namespace ClaimFlow.Data.Entities;

public sealed class Claim
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Payer-assigned business claim identifier.</summary>
    public string ClaimId { get; set; } = string.Empty;

    public string PayerId { get; set; } = string.Empty;

    public string MemberId { get; set; } = string.Empty;

    public Guid? PatientId { get; set; }

    public string ProviderNpi { get; set; } = string.Empty;

    public string PlaceOfService { get; set; } = string.Empty;

    public DateOnly ServiceDate { get; set; }

    public int TotalChargeCents { get; set; }

    public ClaimStatus Status { get; set; } = ClaimStatus.Received;

    public AdjudicationOutcome? Outcome { get; set; }

    public DenialReasonCode? DenialReasonCode { get; set; }

    public int AllowedCents { get; set; }

    public int PaidCents { get; set; }

    public int PatientResponsibilityCents { get; set; }

    /// <summary>Comma-separated validation rule codes; present on rejected claims.</summary>
    public string ValidationRuleCodes { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Epoch milliseconds for portable, indexable ordering (SQLite cannot ORDER BY DateTimeOffset).</summary>
    public long ReceivedAtUnixMs { get; set; }

    public DateTimeOffset LastStatusChangeAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? AdjudicatedAt { get; set; }

    public Patient? Patient { get; set; }

    public List<ClaimLine> Lines { get; set; } = new();

    public List<ClaimStatusHistory> StatusHistory { get; set; } = new();
}

public sealed class ClaimLine
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClaimId { get; set; }

    public Claim? Claim { get; set; }

    public int LineNumber { get; set; }

    public string CptCode { get; set; } = string.Empty;

    public string Icd10Code { get; set; } = string.Empty;

    public string PlaceOfService { get; set; } = string.Empty;

    public int ChargeCents { get; set; }

    public int Units { get; set; } = 1;

    public int AllowedCents { get; set; }

    public int PaidCents { get; set; }

    public int PatientResponsibilityCents { get; set; }
}

public sealed class Patient
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string MemberId { get; set; } = string.Empty;

    public string GivenName { get; set; } = string.Empty;

    public string FamilyName { get; set; } = string.Empty;

    public DateOnly DateOfBirth { get; set; }

    public string SsnLast4 { get; set; } = string.Empty;

    public string SsnHash { get; set; } = string.Empty;

    public string AddressLine1 { get; set; } = string.Empty;

    public string City { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public string PostalCode { get; set; } = string.Empty;
}

public sealed class Payer
{
    public string PayerId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int FilingLimitDays { get; set; }

    public int FeeSchedulePercent { get; set; }
}

public sealed class ClaimStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClaimId { get; set; }

    public Claim? Claim { get; set; }

    public ClaimStatus Status { get; set; }

    /// <summary>Monotonic ordering within a claim; avoids timestamp ties in history reads.</summary>
    public int Sequence { get; set; }

    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Reason { get; set; } = string.Empty;

    public string Actor { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Consumer event id; used to make status updates idempotent.</summary>
    public Guid? EventId { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string EventType { get; set; } = string.Empty;

    public string Payload { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Consumer-side inbox used for idempotent processing by the adjudication service.</summary>
public sealed class ProcessedEvent
{
    public Guid EventId { get; set; }

    public string ClaimId { get; set; } = string.Empty;

    public string Consumer { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; set; } = DateTimeOffset.UtcNow;
}
