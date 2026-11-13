using ClaimFlow.Shared.Reporting;

namespace ClaimFlow.Bench;

public sealed record BenchmarkResults(
    string GeneratedAtUtc,
    string DatabaseBackend,
    string MessagingBackend,
    string Dataset,
    int RequestedClaims,
    int Concurrency,
    IngestMetrics Ingest,
    IReadOnlyList<RejectRuleMetric> RejectsByRule,
    IReadOnlyList<OutcomeMetric> AdjudicationOutcomes,
    IReadOnlyList<DenialRollupRow> TopDenialReasons,
    ArMetrics DaysInAr,
    IReadOnlyList<DaysInArRow> DaysInArByPayer,
    LatencyMetrics EndToEndLatencyMs,
    IReadOnlyList<ReportCallMetric> ReportCalls,
    RollupIndexComparison RollupQuery,
    IReadOnlyList<ThroughputRow> ThroughputByHour);

public sealed record IngestMetrics(
    int Total,
    int Accepted,
    int Rejected,
    int Duplicates,
    int UnexpectedErrors,
    double CleanClaimRatePercent,
    double ElapsedSeconds,
    double ClaimsPerSecond,
    double RequestP50Ms,
    double RequestP95Ms,
    double RequestP99Ms);

public sealed record RejectRuleMetric(
    string RuleCode,
    string Description,
    int ClaimCount,
    double PercentOfSubmissions);

public sealed record OutcomeMetric(string Outcome, int Count, double Percent);

public sealed record ArBucket(string Label, int ClaimCount, long AmountCents);

public sealed record ArMetrics(
    string AsOfDate,
    int OpenClaimCount,
    long OpenArCents,
    double AverageDaysInAr,
    double MedianDaysInAr,
    IReadOnlyList<ArBucket> Buckets);

public sealed record LatencyMetrics(
    int SampleCount,
    double Average,
    double P50,
    double P95,
    double P99,
    double Max);

public sealed record ReportCallMetric(string Name, int HttpStatus, double ElapsedMs, int RowCount);

public sealed record RollupIndexComparison(
    int Iterations,
    double MedianWithoutIndexMs,
    double MedianWithIndexMs,
    double ImprovementPercent,
    int RowsReturned);
