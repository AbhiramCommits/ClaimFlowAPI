using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaimFlow.Bench;

public static class ResultsWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task WriteAsync(
        BenchmarkResults results,
        BenchOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var jsonPath = Path.Combine(options.OutputDirectory, "benchmark.json");
        var reportPath = Path.Combine(options.OutputDirectory, "REPORT.md");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(results, Json), cancellationToken);
        await File.WriteAllTextAsync(reportPath, BuildMarkdown(results, options), cancellationToken);

        Console.WriteLine($"Wrote {jsonPath}");
        Console.WriteLine($"Wrote {reportPath}");
    }

    public static void PrintSummary(BenchmarkResults results)
    {
        var topDenial = results.TopDenialReasons.FirstOrDefault();
        Console.WriteLine();
        Console.WriteLine("=== ClaimFlow benchmark ===");
        Console.WriteLine($"Backend            : {results.DatabaseBackend} + {results.MessagingBackend}");
        Console.WriteLine($"Claims ingested    : {results.Ingest.Total:N0}");
        Console.WriteLine($"Accepted / rejected: {results.Ingest.Accepted:N0} / {results.Ingest.Rejected:N0} ({results.Ingest.CleanClaimRatePercent:N2}% clean)");
        Console.WriteLine($"Ingest throughput  : {results.Ingest.ClaimsPerSecond:N2} claims/sec");
        Console.WriteLine($"End-to-end p95     : {results.EndToEndLatencyMs.P95:N0} ms");
        Console.WriteLine($"Top denial reason  : {(topDenial is null ? "none" : $"{topDenial.DenialCode} ({topDenial.PercentOfDenials:N2}% of denials)")}");
        Console.WriteLine($"Days in A/R        : avg {results.DaysInAr.AverageDaysInAr:N1}, median {results.DaysInAr.MedianDaysInAr:N1}");
        Console.WriteLine($"Rollup query       : {results.RollupQuery.MedianWithoutIndexMs:N2} ms -> {results.RollupQuery.MedianWithIndexMs:N2} ms ({results.RollupQuery.ImprovementPercent:N1}% faster)");
    }

    private static string BuildMarkdown(BenchmarkResults results, BenchOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# ClaimFlowAPI benchmark report");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{results.GeneratedAtUtc}`");
        builder.AppendLine();
        builder.AppendLine("All values below were measured by `tools/ClaimFlow.Bench` in this run. No placeholders.");
        builder.AppendLine();
        builder.AppendLine("| Setting | Value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| Database | {results.DatabaseBackend} |");
        builder.AppendLine($"| Message bus | {results.MessagingBackend} |");
        builder.AppendLine($"| Dataset | `{results.Dataset}` |");
        builder.AppendLine($"| Requested claims | {results.RequestedClaims:N0} |");
        builder.AppendLine($"| Ingest concurrency | {results.Concurrency} |");
        builder.AppendLine();

        builder.AppendLine("## Headline");
        builder.AppendLine();
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| Claims ingested | {results.Ingest.Total:N0} |");
        builder.AppendLine($"| Accepted / rejected / duplicates | {results.Ingest.Accepted:N0} / {results.Ingest.Rejected:N0} / {results.Ingest.Duplicates:N0} |");
        builder.AppendLine($"| Clean-claim rate | {results.Ingest.CleanClaimRatePercent:N2}% |");
        builder.AppendLine($"| Ingest throughput | {results.Ingest.ClaimsPerSecond:N2} claims/sec |");
        builder.AppendLine($"| Ingest wall time | {results.Ingest.ElapsedSeconds:N2} s |");
        builder.AppendLine($"| Per-request p50 / p95 / p99 | {results.Ingest.RequestP50Ms:N1} / {results.Ingest.RequestP95Ms:N1} / {results.Ingest.RequestP99Ms:N1} ms |");
        builder.AppendLine($"| End-to-end p50 / p95 / p99 | {results.EndToEndLatencyMs.P50:N0} / {results.EndToEndLatencyMs.P95:N0} / {results.EndToEndLatencyMs.P99:N0} ms |");
        builder.AppendLine($"| Avg / median days in A/R | {results.DaysInAr.AverageDaysInAr:N1} / {results.DaysInAr.MedianDaysInAr:N1} days |");
        builder.AppendLine($"| Open A/R at report time | {results.DaysInAr.OpenClaimCount:N0} claims, {Money(results.DaysInAr.OpenArCents)} |");
        var top = results.TopDenialReasons.FirstOrDefault();
        builder.AppendLine($"| Top denial reason | {(top is null ? "none" : $"{top.DenialCode} - {top.Description} ({top.PercentOfDenials:N2}%, {top.ClaimCount:N0} claims)")} |");
        builder.AppendLine($"| Denial rollup query | {results.RollupQuery.MedianWithoutIndexMs:N2} ms without 030 indexes -> {results.RollupQuery.MedianWithIndexMs:N2} ms with them ({results.RollupQuery.ImprovementPercent:N1}% improvement) |");
        builder.AppendLine();

        builder.AppendLine("## Rejections by validation rule");
        builder.AppendLine();
        if (results.RejectsByRule.Count == 0)
        {
            builder.AppendLine("No rejections.");
        }
        else
        {
            builder.AppendLine("| Rule | Description | Claims | % of submissions |");
            builder.AppendLine("|---|---|---:|---:|");
            foreach (var row in results.RejectsByRule)
            {
                builder.AppendLine($"| {row.RuleCode} | {row.Description} | {row.ClaimCount:N0} | {row.PercentOfSubmissions:N2}% |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Adjudication outcome mix");
        builder.AppendLine();
        builder.AppendLine("| Outcome | Claims | % of accepted |");
        builder.AppendLine("|---|---:|---:|");
        foreach (var row in results.AdjudicationOutcomes)
        {
            builder.AppendLine($"| {row.Outcome} | {row.Count:N0} | {row.Percent:N2}% |");
        }

        builder.AppendLine();
        builder.AppendLine("## Top denial reasons");
        builder.AppendLine();
        builder.AppendLine("| Rank | Code | Description | Claims | Charge amount | % of denials |");
        builder.AppendLine("|---:|---|---|---:|---:|---:|");
        foreach (var row in results.TopDenialReasons)
        {
            builder.AppendLine($"| {row.Rank} | {row.DenialCode} | {row.Description} | {row.ClaimCount:N0} | {Money(row.ChargeCents)} | {row.PercentOfDenials:N2}% |");
        }

        builder.AppendLine();
        builder.AppendLine("## Days in A/R");
        builder.AppendLine();
        builder.AppendLine($"As of `{results.DaysInAr.AsOfDate}`. Open A/R includes unadjudicated claims and patient-responsibility balances on paid claims.");
        builder.AppendLine();
        builder.AppendLine("| Aging bucket | Claims | A/R amount |");
        builder.AppendLine("|---|---:|---:|");
        foreach (var bucket in results.DaysInAr.Buckets)
        {
            builder.AppendLine($"| {bucket.Label} days | {bucket.ClaimCount:N0} | {Money(bucket.AmountCents)} |");
        }

        builder.AppendLine();
        if (results.DaysInArByPayer.Count > 0)
        {
            builder.AppendLine("| Payer | Open claims | Open A/R | Avg days | Median days |");
            builder.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var row in results.DaysInArByPayer)
            {
                builder.AppendLine($"| {row.PayerName} | {row.OpenClaimCount:N0} | {Money(row.OpenArCents)} | {row.AverageDaysInAr:N1} | {row.MedianDaysInAr:N1} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## End-to-end latency");
        builder.AppendLine();
        builder.AppendLine($"Samples: {results.EndToEndLatencyMs.SampleCount:N0} adjudicated claims, measured from `ReceivedAt` to `AdjudicatedAt`.");
        builder.AppendLine();
        builder.AppendLine("| Percentile | Latency |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| p50 | {results.EndToEndLatencyMs.P50:N0} ms |");
        builder.AppendLine($"| p95 | {results.EndToEndLatencyMs.P95:N0} ms |");
        builder.AppendLine($"| p99 | {results.EndToEndLatencyMs.P99:N0} ms |");
        builder.AppendLine($"| max | {results.EndToEndLatencyMs.Max:N0} ms |");

        builder.AppendLine();
        builder.AppendLine("## Reporting endpoints");
        builder.AppendLine();
        builder.AppendLine("| Endpoint | HTTP | Rows | Warm-cache latency |");
        builder.AppendLine("|---|---:|---:|---:|");
        foreach (var call in results.ReportCalls)
        {
            builder.AppendLine($"| `/api/v1/reports/{call.Name}` | {call.HttpStatus} | {call.RowCount:N0} | {call.ElapsedMs:N2} ms |");
        }

        builder.AppendLine();
        builder.AppendLine("## Denial rollup: index impact");
        builder.AppendLine();
        builder.AppendLine("`usp_DenialReasonRollup` executed five times after a warm-up, median reported. The 030 indexes were dropped and recreated between measurements.");
        builder.AppendLine();
        builder.AppendLine("| Measurement | Median |");
        builder.AppendLine("|---|---:|");
        builder.AppendLine($"| Without 030 indexes | {results.RollupQuery.MedianWithoutIndexMs:N2} ms |");
        builder.AppendLine($"| With 030 indexes | {results.RollupQuery.MedianWithIndexMs:N2} ms |");
        builder.AppendLine($"| Improvement | {results.RollupQuery.ImprovementPercent:N2}% |");
        builder.AppendLine();
        builder.AppendLine($"Rows returned: {results.RollupQuery.RowsReturned}.");

        builder.AppendLine();
        builder.AppendLine("## Throughput by hour");
        builder.AppendLine();
        builder.AppendLine("| Hour (UTC) | Ingested | Adjudicated |");
        builder.AppendLine("|---|---:|---:|");
        foreach (var row in results.ThroughputByHour)
        {
            builder.AppendLine($"| {row.HourStartUtc:yyyy-MM-dd HH:mm} | {row.IngestedCount:N0} | {row.AdjudicatedCount:N0} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Reproduce");
        builder.AppendLine();
        builder.AppendLine("```bash");
        builder.AppendLine("dotnet build");
        builder.AppendLine("export CLAIMFLOW_SQL_PASSWORD='<local SQL Server password>'");
        builder.AppendLine("export RABBITMQ_PASSWORD='<local RabbitMQ password>'");
        builder.AppendLine($"dotnet run --project tools/ClaimFlow.Bench -- --claims {options.Claims} --concurrency {options.Concurrency}");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("The harness resets the database, applies EF migrations and `db/*.sql`, purges the RabbitMQ queues, starts both services, ingests the dataset over HTTP, waits for full adjudication, calls every report endpoint, and times the rollup with and without the reporting indexes.");

        return builder.ToString();
    }

    private static string Money(long cents) =>
        (cents / 100m).ToString("C2", CultureInfo.GetCultureInfo("en-US"));
}
