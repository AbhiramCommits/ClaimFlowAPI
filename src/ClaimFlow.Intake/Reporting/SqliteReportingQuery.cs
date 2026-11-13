using ClaimFlow.Data;
using ClaimFlow.Shared;
using ClaimFlow.Shared.Reporting;
using ClaimFlow.Shared.Validation;
using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Intake.Reporting;

/// <summary>LINQ equivalent of the T-SQL reporting procs, used for SQLite and tests.</summary>
public sealed class SqliteReportingQuery : IReportingQuery
{
    private readonly ClaimFlowDbContext _db;

    public SqliteReportingQuery(ClaimFlowDbContext db) => _db = db;

    public async Task<IReadOnlyList<DenialRollupRow>> GetDenialRollupAsync(
        DateOnly fromDate,
        DateOnly toDate,
        string? payerId,
        CancellationToken cancellationToken)
    {
        var query = _db.Claims.AsNoTracking()
            .Where(c => c.Status != ClaimStatus.Rejected &&
                        c.DenialReasonCode != null &&
                        c.ServiceDate >= fromDate &&
                        c.ServiceDate <= toDate);

        if (!string.IsNullOrWhiteSpace(payerId))
        {
            var payer = payerId.Trim().ToUpperInvariant();
            query = query.Where(c => c.PayerId == payer);
        }

        var grouped = await query
            .GroupBy(c => c.DenialReasonCode!.Value)
            .Select(g => new
            {
                Code = g.Key,
                ClaimCount = g.Count(),
                ChargeCents = g.Sum(c => (long)c.TotalChargeCents),
            })
            .ToListAsync(cancellationToken);

        var total = grouped.Sum(g => (long)g.ClaimCount);
        var sorted = grouped.OrderByDescending(g => g.ClaimCount).ThenBy(g => g.Code).ToArray();
        var rows = new List<DenialRollupRow>(sorted.Length);
        var rank = 0;
        var previousCount = -1;

        for (var i = 0; i < sorted.Length; i++)
        {
            var group = sorted[i];
            if (group.ClaimCount != previousCount)
            {
                rank = i + 1;
                previousCount = group.ClaimCount;
            }

            rows.Add(new DenialRollupRow(
                DenialReasons.Code(group.Code),
                DenialReasons.Describe(group.Code),
                group.ClaimCount,
                group.ChargeCents,
                total == 0 ? 0m : Math.Round(100m * group.ClaimCount / total, 2),
                rank));
        }

        return rows;
    }

    public async Task<IReadOnlyList<DaysInArRow>> GetDaysInArAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        var claims = await _db.Claims.AsNoTracking()
            .Where(c => c.Status != ClaimStatus.Rejected &&
                        (c.Status == ClaimStatus.Validated ||
                         c.Status == ClaimStatus.Adjudicated ||
                         (c.Status == ClaimStatus.Paid && c.PatientResponsibilityCents > 0)))
            .Select(c => new
            {
                c.PayerId,
                c.Status,
                c.ServiceDate,
                c.TotalChargeCents,
                c.PaidCents,
                c.PatientResponsibilityCents,
            })
            .ToListAsync(cancellationToken);

        var payerNames = await _db.Payers.AsNoTracking()
            .ToDictionaryAsync(p => p.PayerId, p => p.Name, cancellationToken);

        var rows = new List<DaysInArRow>();
        foreach (var group in claims.GroupBy(c => c.PayerId).OrderByDescending(g => g.Count()))
        {
            var days = group
                .Select(c => Math.Max(0, asOfDate.DayNumber - c.ServiceDate.DayNumber))
                .ToArray();
            var outstanding = group.Select(c =>
                c.Status is ClaimStatus.Validated or ClaimStatus.Adjudicated
                    ? (long)c.TotalChargeCents
                    : c.PatientResponsibilityCents).ToArray();

            var buckets = new (int Claims, long Cents)[4];
            for (var i = 0; i < days.Length; i++)
            {
                var bucket = days[i] switch
                {
                    <= 30 => 0,
                    <= 60 => 1,
                    <= 90 => 2,
                    _ => 3,
                };
                buckets[bucket] = (buckets[bucket].Claims + 1, buckets[bucket].Cents + outstanding[i]);
            }

            var sortedDays = days.OrderBy(d => d).ToArray();
            var median = sortedDays.Length == 0
                ? 0m
                : sortedDays.Length % 2 == 1
                    ? sortedDays[sortedDays.Length / 2]
                    : (sortedDays[(sortedDays.Length / 2) - 1] + sortedDays[sortedDays.Length / 2]) / 2m;

            rows.Add(new DaysInArRow(
                group.Key,
                payerNames.GetValueOrDefault(group.Key, group.Key),
                days.Length,
                outstanding.Sum(),
                days.Length == 0 ? 0m : Math.Round((decimal)days.Average(), 2),
                Math.Round(median, 2),
                buckets[0].Claims, buckets[0].Cents,
                buckets[1].Claims, buckets[1].Cents,
                buckets[2].Claims, buckets[2].Cents,
                buckets[3].Claims, buckets[3].Cents));
        }

        return rows;
    }

    public async Task<IReadOnlyList<ValidationRejectRow>> GetValidationRejectsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        var from = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toExclusive = new DateTimeOffset(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var claims = await _db.Claims.AsNoTracking()
            .Select(c => new { c.Status, c.ReceivedAt, c.ValidationRuleCodes })
            .ToListAsync(cancellationToken);

        var submissions = claims.Count(c => c.ReceivedAt >= from && c.ReceivedAt < toExclusive);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var claim in claims.Where(c => c.Status == ClaimStatus.Rejected &&
                                                c.ReceivedAt >= from && c.ReceivedAt < toExclusive))
        {
            foreach (var code in claim.ValidationRuleCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                counts[code] = counts.GetValueOrDefault(code) + 1;
            }
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ValidationRejectRow(
                pair.Key,
                ValidationRuleCodes.Descriptions.GetValueOrDefault(pair.Key, pair.Key),
                pair.Value,
                submissions == 0 ? 0m : Math.Round(100m * pair.Value / submissions, 2)))
            .ToArray();
    }

    public async Task<IReadOnlyList<ThroughputRow>> GetThroughputAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        var from = new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toExclusive = new DateTimeOffset(toDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var claims = await _db.Claims.AsNoTracking()
            .Select(c => new { c.ReceivedAt, c.AdjudicatedAt })
            .ToListAsync(cancellationToken);

        var ingest = claims
            .Where(c => c.ReceivedAt >= from && c.ReceivedAt < toExclusive)
            .GroupBy(c => TruncateToHour(c.ReceivedAt.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Count());

        var adjudicated = claims
            .Where(c => c.AdjudicatedAt != null &&
                        c.AdjudicatedAt >= from && c.AdjudicatedAt < toExclusive)
            .GroupBy(c => TruncateToHour(c.AdjudicatedAt!.Value.UtcDateTime))
            .ToDictionary(g => g.Key, g => g.Count());

        return ingest.Keys.Union(adjudicated.Keys)
            .OrderBy(hour => hour)
            .Select(hour => new ThroughputRow(
                hour,
                ingest.GetValueOrDefault(hour),
                adjudicated.GetValueOrDefault(hour)))
            .ToArray();
    }

    private static DateTime TruncateToHour(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
}
