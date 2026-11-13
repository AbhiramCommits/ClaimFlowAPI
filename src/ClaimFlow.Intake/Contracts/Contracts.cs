using ClaimFlow.Shared;

using ClaimFlow.Shared.Validation;

namespace ClaimFlow.Intake.Contracts;

public sealed record ClaimLineDto(
    int LineNumber,
    string CptCode,
    string Icd10Code,
    string PlaceOfService,
    int ChargeCents,
    int Units,
    int AllowedCents,
    int PaidCents,
    int PatientResponsibilityCents);

public sealed record ClaimStatusHistoryDto(
    ClaimStatus Status,
    DateTimeOffset ChangedAt,
    string Reason,
    string Actor);

public sealed record ClaimDetailDto(
    Guid Id,
    string ClaimId,
    string PayerId,
    string MemberId,
    string ProviderNpi,
    string PlaceOfService,
    DateOnly ServiceDate,
    int TotalChargeCents,
    ClaimStatus Status,
    AdjudicationOutcome? Outcome,
    DenialReasonCode? DenialReasonCode,
    int AllowedCents,
    int PaidCents,
    int PatientResponsibilityCents,
    string ValidationRuleCodes,
    string CorrelationId,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? AdjudicatedAt,
    IReadOnlyList<ClaimLineDto> Lines,
    IReadOnlyList<ClaimStatusHistoryDto> StatusHistory);

public sealed record ClaimSummaryDto(
    Guid Id,
    string ClaimId,
    string PayerId,
    string MemberId,
    DateOnly ServiceDate,
    int TotalChargeCents,
    ClaimStatus Status,
    AdjudicationOutcome? Outcome,
    DenialReasonCode? DenialReasonCode,
    int PaidCents,
    int PatientResponsibilityCents,
    DateTimeOffset ReceivedAt);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages);

public sealed record BatchIngestResponse(
    int Total,
    int Accepted,
    int Rejected,
    int Duplicates,
    IReadOnlyList<BatchItemResult> Results);

public sealed record BatchItemResult(
    int Index,
    string ClaimId,
    string Status,
    IReadOnlyList<string> RuleCodes,
    IReadOnlyList<ValidationIssue> Errors);

public sealed class ClaimStatusUpdateRequest
{
    public ClaimStatus Status { get; init; }

    public AdjudicationOutcome? Outcome { get; init; }

    public DenialReasonCode? DenialReasonCode { get; init; }

    public int AllowedCents { get; init; }

    public int PaidCents { get; init; }

    public int PatientResponsibilityCents { get; init; }

    public string? Reason { get; init; }

    public Guid? EventId { get; init; }

    public string? Actor { get; init; }
}
