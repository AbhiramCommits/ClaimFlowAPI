using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaimFlow.Security.Auth;
using ClaimFlow.Shared.Reporting;
using ClaimFlow.Shared.Validation;

namespace ClaimFlow.Bench;

public sealed class BenchmarkRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BenchOptions _options;
    private readonly DatabaseAdmin _database;
    private readonly string _adminToken;
    private readonly string _reportToken;

    public BenchmarkRunner(BenchOptions options, DatabaseAdmin database, JwtOptions jwt)
    {
        _options = options;
        _database = database;
        _adminToken = JwtTokenFactory.CreateToken(jwt, "benchmark", new[] { ClaimFlowRoles.Admin });
        _reportToken = JwtTokenFactory.CreateToken(jwt, "benchmark-reports", new[] { ClaimFlowRoles.ReportingReader });
    }

    public async Task<BenchmarkResults> RunAsync(CancellationToken cancellationToken)
    {
        var allLines = await File.ReadAllLinesAsync(_options.DataPath, cancellationToken);
        if (allLines.Length < _options.Claims)
        {
            throw new InvalidOperationException(
                $"{_options.DataPath} has {allLines.Length} records but --claims requested {_options.Claims}.");
        }

        var records = allLines.Take(_options.Claims).ToArray();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var ingest = await IngestAsync(records, cancellationToken);
        await WaitForDrainAsync(ingest.Accepted, cancellationToken);

        var outcomes = await _database.GetOutcomeMixAsync(cancellationToken);
        var ar = await _database.GetArMetricsAsync(today, cancellationToken);

        using var reportClient = new HttpClient { BaseAddress = new Uri(_options.IntakeUrl), Timeout = TimeSpan.FromMinutes(2) };
        reportClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _reportToken);

        var reportCalls = new List<ReportCallMetric>();
        var reportFrom = today.AddDays(-400);
        var reportTo = today.AddDays(1);

        var denials = await GetReportAsync<List<DenialRollupRow>>(
            reportClient, "denials",
            $"/api/v1/reports/denials?fromDate={reportFrom:yyyy-MM-dd}&toDate={reportTo:yyyy-MM-dd}",
            rows => rows.Count, reportCalls, cancellationToken);

        var arRows = await GetReportAsync<List<DaysInArRow>>(
            reportClient, "days-in-ar",
            $"/api/v1/reports/days-in-ar?asOfDate={today:yyyy-MM-dd}",
            rows => rows.Count, reportCalls, cancellationToken);

        _ = await GetReportAsync<List<ValidationRejectRow>>(
            reportClient, "rejects",
            $"/api/v1/reports/rejects?fromDate={reportFrom:yyyy-MM-dd}&toDate={reportTo:yyyy-MM-dd}",
            rows => rows.Count, reportCalls, cancellationToken);

        var throughput = await GetReportAsync<List<ThroughputRow>>(
            reportClient, "throughput",
            $"/api/v1/reports/throughput?fromDate={today.AddDays(-1):yyyy-MM-dd}&toDate={today.AddDays(1):yyyy-MM-dd}",
            rows => rows.Count, reportCalls, cancellationToken);

        var rollup = await MeasureRollupAsync(reportFrom, today, cancellationToken);
        var latencies = await _database.GetEndToEndLatenciesAsync(cancellationToken);

        var total = _options.Claims;
        var rejectCounts = ingest.Results
            .Where(r => r.StatusCode == (int)HttpStatusCode.UnprocessableEntity)
            .SelectMany(r => r.RuleCodes)
            .GroupBy(code => code, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count());

        var rejectsByRule = rejectCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new RejectRuleMetric(
                pair.Key,
                ValidationRuleCodes.Descriptions.GetValueOrDefault(pair.Key, pair.Key),
                pair.Value,
                Math.Round(100d * pair.Value / total, 2)))
            .ToArray();

        var accepted = ingest.Accepted;
        var outcomeRows = outcomes
            .OrderByDescending(o => o.Count)
            .Select(o => new OutcomeMetric(o.Outcome, o.Count, accepted == 0 ? 0 : Math.Round(100d * o.Count / accepted, 2)))
            .ToArray();

        var requestLatencies = ingest.RequestLatenciesMs;
        var ingestMetrics = new IngestMetrics(
            total,
            ingest.Accepted,
            ingest.Rejected,
            ingest.Duplicates,
            ingest.Errors,
            Math.Round(100d * ingest.Accepted / total, 2),
            Math.Round(ingest.ElapsedSeconds, 2),
            Math.Round(total / ingest.ElapsedSeconds, 2),
            Math.Round(Percentile(requestLatencies, 50), 2),
            Math.Round(Percentile(requestLatencies, 95), 2),
            Math.Round(Percentile(requestLatencies, 99), 2));

        var latencyNumbers = latencies.Select(v => (double)v).ToArray();
        var latencyMetrics = new LatencyMetrics(
            latencyNumbers.Length,
            latencyNumbers.Length == 0 ? 0 : Math.Round(latencyNumbers.Average(), 2),
            Math.Round(Percentile(latencyNumbers, 50), 2),
            Math.Round(Percentile(latencyNumbers, 95), 2),
            Math.Round(Percentile(latencyNumbers, 99), 2),
            latencyNumbers.Length == 0 ? 0 : Math.Round(latencyNumbers.Max(), 2));

        return new BenchmarkResults(
            DateTime.UtcNow.ToString("O"),
            "SQL Server 2022 (mcr.microsoft.com/mssql/server:2022-latest, linux/amd64 container)",
            "RabbitMQ 3.13 (rabbitmq:3-management container)",
            Path.GetRelativePath(_options.RepositoryRoot, _options.DataPath),
            total,
            _options.Concurrency,
            ingestMetrics,
            rejectsByRule,
            outcomeRows,
            denials,
            ar,
            arRows,
            latencyMetrics,
            reportCalls,
            rollup,
            throughput);
    }

    private async Task<IngestRun> IngestAsync(string[] records, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri(_options.IntakeUrl),
            Timeout = TimeSpan.FromMinutes(3),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);

        var results = new ConcurrentBag<IngestRecord>();
        var requestLatencies = new ConcurrentBag<double>();
        var accepted = 0;
        var rejected = 0;
        var duplicates = 0;
        var errors = 0;
        var errorMessages = new ConcurrentQueue<string>();
        var completed = 0;

        Console.WriteLine($"Ingesting {records.Length:N0} claims with concurrency {_options.Concurrency}...");
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, records.Length),
            new ParallelOptions { MaxDegreeOfParallelism = _options.Concurrency, CancellationToken = cancellationToken },
            async (index, token) =>
            {
                var line = records[index];
                var requestWatch = Stopwatch.StartNew();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/claims")
                    {
                        Content = new StringContent(line, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.Add("X-Correlation-Id", $"bench-{index:D6}");

                    using var response = await client.SendAsync(request, token);
                    requestWatch.Stop();
                    requestLatencies.Add(requestWatch.Elapsed.TotalMilliseconds);

                    switch ((int)response.StatusCode)
                    {
                        case 201:
                            Interlocked.Increment(ref accepted);
                            results.Add(new IngestRecord(201, Array.Empty<string>()));
                            break;
                        case 422:
                            {
                                Interlocked.Increment(ref rejected);
                                var body = await response.Content.ReadAsStringAsync(token);
                                var codes = ReadRuleCodes(body);
                                results.Add(new IngestRecord(422, codes));
                                break;
                            }
                        case 409:
                            Interlocked.Increment(ref duplicates);
                            results.Add(new IngestRecord(409, new[] { "CO18" }));
                            break;
                        default:
                            {
                                Interlocked.Increment(ref errors);
                                var body = await response.Content.ReadAsStringAsync(token);
                                results.Add(new IngestRecord((int)response.StatusCode, Array.Empty<string>()));
                                errorMessages.Enqueue($"{(int)response.StatusCode}: {body[..Math.Min(300, body.Length)]}");
                                break;
                            }
                    }
                }
                catch (Exception ex)
                {
                    requestWatch.Stop();
                    Interlocked.Increment(ref errors);
                    errorMessages.Enqueue(ex.Message);
                    results.Add(new IngestRecord(0, Array.Empty<string>()));
                }

                var done = Interlocked.Increment(ref completed);
                if (done % 500 == 0 || done == records.Length)
                {
                    Console.WriteLine(
                        $"  {done:N0}/{records.Length:N0} accepted={Volatile.Read(ref accepted):N0} " +
                        $"rejected={Volatile.Read(ref rejected):N0} " +
                        $"duplicates={Volatile.Read(ref duplicates):N0} errors={Volatile.Read(ref errors):N0}");
                }
            });

        stopwatch.Stop();
        Console.WriteLine($"Ingest finished in {stopwatch.Elapsed.TotalSeconds:N2}s.");

        if (errors > 0)
        {
            throw new InvalidOperationException(
                $"{errors} ingest requests failed unexpectedly. First errors:\n" +
                string.Join("\n", errorMessages.Take(5)));
        }

        return new IngestRun(
            accepted, rejected, duplicates, errors,
            stopwatch.Elapsed.TotalSeconds,
            results.ToArray(),
            requestLatencies.ToArray());
    }

    private async Task WaitForDrainAsync(int accepted, CancellationToken cancellationToken)
    {
        Console.WriteLine("Waiting for adjudication drain...");
        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;

        while (stopwatch.Elapsed < TimeSpan.FromMinutes(15))
        {
            var (validated, outboxPending, processed) = await _database.GetDrainStateAsync(cancellationToken);
            if (validated == 0 && outboxPending == 0 && processed >= accepted)
            {
                await Task.Delay(1500, cancellationToken);
                (validated, outboxPending, processed) = await _database.GetDrainStateAsync(cancellationToken);
                if (validated == 0 && outboxPending == 0 && processed >= accepted)
                {
                    Console.WriteLine(
                        $"Drain complete in {stopwatch.Elapsed.TotalSeconds:N1}s: validated=0, " +
                        $"outboxPending=0, processed={processed:N0}/{accepted:N0}.");
                    return;
                }
            }

            if (stopwatch.Elapsed - lastReport > TimeSpan.FromSeconds(5))
            {
                Console.WriteLine(
                    $"  drain: validated={validated:N0} outboxPending={outboxPending:N0} " +
                    $"processed={processed:N0}/{accepted:N0}");
                lastReport = stopwatch.Elapsed;
            }

            await Task.Delay(500, cancellationToken);
        }

        var state = await _database.GetDrainStateAsync(cancellationToken);
        throw new TimeoutException(
            $"Adjudication did not drain within 15 minutes. validated={state.Validated}, " +
            $"outboxPending={state.OutboxPending}, processedEvents={state.ProcessedEvents}/{accepted}.");
    }

    private async Task<RollupIndexComparison> MeasureRollupAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("Timing usp_DenialReasonRollup without and with the 030 indexes...");

        await _database.EnsureIndexesAsync(cancellationToken);
        await _database.DropReportingIndexesAsync(cancellationToken);
        var without = await MeasureRollupIterationsAsync(fromDate, toDate, cancellationToken);

        await _database.EnsureIndexesAsync(cancellationToken);
        var with = await MeasureRollupIterationsAsync(fromDate, toDate, cancellationToken);

        var improvement = without.MedianMs <= 0
            ? 0
            : Math.Round(100d * (without.MedianMs - with.MedianMs) / without.MedianMs, 2);

        Console.WriteLine(
            $"  rollup without index: {without.MedianMs:N2} ms; with index: {with.MedianMs:N2} ms " +
            $"({improvement:N2}% improvement)");

        return new RollupIndexComparison(5, without.MedianMs, with.MedianMs, improvement, with.Rows);
    }

    private async Task<(double MedianMs, int Rows)> MeasureRollupIterationsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        _ = await _database.RunDenialRollupAsync(fromDate, toDate, cancellationToken);
        var samples = new List<double>();
        var rows = 0;
        for (var i = 0; i < 5; i++)
        {
            var (result, elapsed) = await _database.RunDenialRollupAsync(fromDate, toDate, cancellationToken);
            samples.Add(elapsed);
            rows = result.Count;
        }

        samples.Sort();
        return (Math.Round(samples[samples.Count / 2], 3), rows);
    }

    private static async Task<T> GetReportAsync<T>(
        HttpClient client,
        string name,
        string path,
        Func<T, int> countRows,
        List<ReportCallMetric> calls,
        CancellationToken cancellationToken)
        where T : class
    {
        _ = await client.GetAsync(path, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync(path, cancellationToken);
        stopwatch.Stop();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Report '{name}' returned {(int)response.StatusCode}: {body}");
        }

        var value = JsonSerializer.Deserialize<T>(body, Json)
            ?? throw new InvalidOperationException($"Report '{name}' returned an empty body.");
        calls.Add(new ReportCallMetric(name, (int)response.StatusCode, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2), countRows(value)));
        return value;
    }

    private static IReadOnlyList<string> ReadRuleCodes(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("ruleCodes", out var codes) &&
                codes.ValueKind == JsonValueKind.Array)
            {
                return codes.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
        }
        catch (JsonException)
        {
            // Fall through.
        }

        return Array.Empty<string>();
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToArray();
        var rank = (int)Math.Ceiling(percentile / 100d * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private sealed record IngestRecord(int StatusCode, IReadOnlyList<string> RuleCodes);

    private sealed record IngestRun(
        int Accepted,
        int Rejected,
        int Duplicates,
        int Errors,
        double ElapsedSeconds,
        IReadOnlyList<IngestRecord> Results,
        IReadOnlyList<double> RequestLatenciesMs);
}
