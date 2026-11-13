namespace ClaimFlow.Shared.Domain;

/// <summary>Canonical, validated claim shape produced by <c>ClaimNormalizer</c>.</summary>
public sealed record NormalizedClaim(
    string ClaimId,
    string PayerId,
    string MemberId,
    string ProviderNpi,
    string PlaceOfService,
    DateOnly ServiceDate,
    int TotalChargeCents,
    IReadOnlyList<NormalizedClaimLine> Lines,
    IReadOnlyList<string> DiagnosisCodes,
    string CorrelationId);

public sealed record NormalizedClaimLine(
    int LineNumber,
    string CptCode,
    string Icd10Code,
    string PlaceOfService,
    int ChargeCents,
    int Units);

/// <summary>Patient data reduced to the minimum needed downstream. SSN is stored only as last-4 + hash.</summary>
public sealed record NormalizedPatient(
    string MemberId,
    string GivenName,
    string FamilyName,
    DateOnly DateOfBirth,
    string SsnLast4,
    string SsnHash,
    NormalizedAddress Address);

public sealed record NormalizedAddress(string Line1, string City, string State, string PostalCode);

public sealed record NormalizedPayer(string PayerId, string Name, int FilingLimitDays);

public sealed record NormalizedCoverage(
    string CoverageId,
    string PayerId,
    CoverageStatus Status,
    DateOnly EffectiveDate,
    DateOnly? TerminationDate,
    int DeductibleRemainingCents);

public sealed record NormalizationResult(
    NormalizedClaim Claim,
    NormalizedPatient Patient,
    NormalizedPayer Payer,
    NormalizedCoverage Coverage);

/// <summary>Immutable payer master data shared by intake validation and adjudication.</summary>
public sealed record PayerProfile(
    string PayerId,
    string Name,
    int FilingLimitDays,
    int FeeSchedulePercent,
    int CoveragePercent);
