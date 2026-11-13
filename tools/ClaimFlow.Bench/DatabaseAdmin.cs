using System.Data;
using System.Data.Common;
using System.Diagnostics;
using ClaimFlow.Data;
using ClaimFlow.Shared.Reporting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Bench;

/// <summary>Owns database reset, schema migration, T-SQL scripts, and measured queries.</summary>
public sealed class DatabaseAdmin
{
    private readonly BenchOptions _options;

    public DatabaseAdmin(BenchOptions options) => _options = options;

    public async Task ResetAndPrepareAsync(CancellationToken cancellationToken)
    {
        if (!_options.SkipReset)
        {
            await ForceDropDatabaseAsync(cancellationToken);
        }

        var dbOptions = new DbContextOptionsBuilder<ClaimFlowDbContext>()
            .UseSqlServer(_options.ConnectionString)
            .Options;
        await using var db = new ClaimFlowDbContext(dbOptions);
        await db.Database.MigrateAsync(cancellationToken);

        await ApplyScriptsAsync(cancellationToken);
    }

    public async Task ApplyScriptsAsync(CancellationToken cancellationToken)
    {
        foreach (var file in new[]
                 {
                     "db/001_schema_notes.sql",
                     "db/010_views.sql",
                     "db/020_procs.sql",
                     "db/030_indexes.sql",
                 })
        {
            var path = Path.Combine(_options.RepositoryRoot, file);
            var text = await File.ReadAllTextAsync(path, cancellationToken);
            await ExecuteBatchesAsync(text, cancellationToken, timeoutSeconds: 120);
            Console.WriteLine($"Applied {file}");
        }
    }

    public async Task DropReportingIndexesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            DROP INDEX IF EXISTS IX_Claims_Reporting_Denials ON dbo.Claims;
            DROP INDEX IF EXISTS IX_Claims_Reporting_Denials_Payer ON dbo.Claims;
            DROP INDEX IF EXISTS IX_Claims_Reporting_Ar ON dbo.Claims;
            DROP INDEX IF EXISTS IX_Claims_Reporting_Rejects ON dbo.Claims;
            DROP INDEX IF EXISTS IX_Claims_Reporting_Throughput ON dbo.Claims;
            """;
        await ExecuteAsync(sql, cancellationToken, timeoutSeconds: 120);
    }

    public async Task<(int Validated, int OutboxPending, int ProcessedEvents)> GetDrainStateAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM dbo.Claims WHERE Status = 'Validated'),
                (SELECT COUNT(*) FROM dbo.OutboxMessages WHERE ProcessedAt IS NULL),
                (SELECT COUNT(*) FROM dbo.ProcessedEvents);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public async Task<IReadOnlyList<long>> GetEndToEndLatenciesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DATEDIFF_BIG(millisecond, ReceivedAt, AdjudicatedAt)
            FROM dbo.Claims
            WHERE AdjudicatedAt IS NOT NULL;
            """;
        var values = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetInt64(0));
        }

        return values;
    }

    public async Task<IReadOnlyList<(string Outcome, int Count)>> GetOutcomeMixAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ISNULL(Outcome, '(none)') AS Outcome, COUNT(*) AS ClaimCount
            FROM dbo.Claims
            WHERE Status <> 'Rejected'
            GROUP BY Outcome;
            """;
        var rows = new List<(string, int)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return rows;
    }

    public async Task<ArMetrics> GetArMetricsAsync(DateOnly asOfDate, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.ServiceDate, c.Status, c.TotalChargeCents, c.PaidCents, c.PatientResponsibilityCents
            FROM dbo.Claims AS c
            WHERE c.Status <> 'Rejected'
              AND (c.Status IN ('Validated', 'Adjudicated')
                   OR (c.Status = 'Paid' AND c.PatientResponsibilityCents > 0));
            """;
        var days = new List<int>();
        var cents = new List<long>();
        var buckets = new (int Count, long Cents)[4];

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var serviceDate = DateOnly.FromDateTime(reader.GetDateTime(0));
            var status = reader.GetString(1);
            var total = reader.GetInt32(2);
            var patient = reader.GetInt32(4);
            var daysInAr = Math.Max(0, asOfDate.DayNumber - serviceDate.DayNumber);
            var openCents = status is "Validated" or "Adjudicated" ? total : patient;

            days.Add(daysInAr);
            cents.Add(openCents);
            var bucket = daysInAr switch
            {
                <= 30 => 0,
                <= 60 => 1,
                <= 90 => 2,
                _ => 3,
            };
            buckets[bucket] = (buckets[bucket].Count + 1, buckets[bucket].Cents + openCents);
        }

        var sorted = days.OrderBy(d => d).ToArray();
        var median = sorted.Length == 0
            ? 0d
            : sorted.Length % 2 == 1
                ? sorted[sorted.Length / 2]
                : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2d;

        return new ArMetrics(
            asOfDate.ToString("yyyy-MM-dd"),
            days.Count,
            cents.Sum(),
            days.Count == 0 ? 0 : Math.Round(days.Average(), 2),
            Math.Round(median, 2),
            new[]
            {
                new ArBucket("0-30", buckets[0].Count, buckets[0].Cents),
                new ArBucket("31-60", buckets[1].Count, buckets[1].Cents),
                new ArBucket("61-90", buckets[2].Count, buckets[2].Cents),
                new ArBucket("90+", buckets[3].Count, buckets[3].Cents),
            });
    }

    public async Task<(IReadOnlyList<DenialRollupRow> Rows, double ElapsedMs)> RunDenialRollupAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "dbo.usp_DenialReasonRollup";
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = 120;
        command.Parameters.Add(new SqlParameter("@FromDate", fromDate.ToDateTime(TimeOnly.MinValue)));
        command.Parameters.Add(new SqlParameter("@ToDate", toDate.ToDateTime(TimeOnly.MinValue)));
        command.Parameters.Add(new SqlParameter("@PayerId", DBNull.Value));

        var stopwatch = Stopwatch.StartNew();
        var rows = new List<DenialRollupRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DenialRollupRow(
                reader.GetString(reader.GetOrdinal("DenialCode")),
                reader.GetString(reader.GetOrdinal("Description")),
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("ClaimCount"))),
                Convert.ToInt64(reader.GetValue(reader.GetOrdinal("ChargeCents"))),
                Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("PercentOfDenials"))),
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("Rank")))));
        }

        stopwatch.Stop();
        return (rows, stopwatch.Elapsed.TotalMilliseconds);
    }

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(
            Path.Combine(_options.RepositoryRoot, "db/030_indexes.sql"), cancellationToken);
        await ExecuteBatchesAsync(text, cancellationToken, timeoutSeconds: 120);
    }

    private async Task ForceDropDatabaseAsync(CancellationToken cancellationToken)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = _options.ConnectionString };
        var database = builder["Database"]?.ToString() ?? "ClaimFlowBench";
        if (!database.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            throw new InvalidOperationException("Database name must be alphanumeric/underscore for the bench reset.");
        }

        builder["Database"] = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"""
            IF DB_ID(N'{database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{database}];
            END
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        Console.WriteLine($"Dropped and will recreate database {database}.");
    }

    private async Task ExecuteBatchesAsync(string script, CancellationToken cancellationToken, int timeoutSeconds)
    {
        foreach (var batch in SplitOnGo(script))
        {
            if (string.IsNullOrWhiteSpace(batch))
            {
                continue;
            }

            await ExecuteAsync(batch, cancellationToken, timeoutSeconds);
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, int timeoutSeconds)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IEnumerable<string> SplitOnGo(string script)
    {
        var batch = new System.Text.StringBuilder();
        using var reader = new StringReader(script);
        while (reader.ReadLine() is { } line)
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                yield return batch.ToString();
                batch.Clear();
                continue;
            }

            batch.AppendLine(line);
        }

        yield return batch.ToString();
    }
}
