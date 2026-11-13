namespace ClaimFlow.Shared.Reporting;

public sealed record DenialRollupRow(
    string DenialCode,
    string Description,
    int ClaimCount,
    long ChargeCents,
    decimal PercentOfDenials,
    int Rank);

public sealed record DaysInArRow(
    string PayerId,
    string PayerName,
    int OpenClaimCount,
    long OpenArCents,
    decimal AverageDaysInAr,
    decimal MedianDaysInAr,
    int Bucket0To30Claims,
    long Bucket0To30Cents,
    int Bucket31To60Claims,
    long Bucket31To60Cents,
    int Bucket61To90Claims,
    long Bucket61To90Cents,
    int Bucket90PlusClaims,
    long Bucket90PlusCents);

public sealed record ValidationRejectRow(
    string RuleCode,
    string Description,
    int RejectCount,
    decimal PercentOfSubmissions);

public sealed record ThroughputRow(
    DateTime HourStartUtc,
    int IngestedCount,
    int AdjudicatedCount);
