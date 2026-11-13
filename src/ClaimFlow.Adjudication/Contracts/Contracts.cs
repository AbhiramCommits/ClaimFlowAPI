using ClaimFlow.Adjudication.Services;
using ClaimFlow.Shared;

namespace ClaimFlow.Adjudication.Contracts;

public sealed record EligibilityCheckRequest(string MemberId, string PayerId, DateOnly ServiceDate);

public sealed record EligibilityCheckResponse(
    bool Eligible,
    EligibilityStatus Status,
    DenialReasonCode? DenialReasonCode,
    int DeductibleRemainingCents,
    DateOnly? TerminationDate,
    string Message);

public sealed record DryRunLine(string CptCode, string Icd10Code, int ChargeCents);

public sealed record DryRunRequest(
    string? ClaimId,
    string PayerId,
    string MemberId,
    DateOnly ServiceDate,
    IReadOnlyList<DryRunLine> Lines,
    IReadOnlyList<string> DiagnosisCodes);

public sealed record DryRunResponse(
    string ClaimId,
    AdjudicationOutcome Outcome,
    DenialReasonCode? DenialReasonCode,
    int AllowedCents,
    int PaidCents,
    int PatientResponsibilityCents,
    IReadOnlyList<AdjudicationLineResult> Lines,
    IReadOnlyList<string> AppliedRules,
    EligibilityCheckResponse Eligibility);
