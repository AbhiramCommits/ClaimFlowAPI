using System.Data;
using System.Data.Common;
using ClaimFlow.Data;
using ClaimFlow.Shared.Reporting;
using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Intake.Reporting;

/// <summary>Executes the T-SQL reporting procedures in db/ through ADO.NET.</summary>
public sealed class SqlServerReportingQuery : IReportingQuery
{
    private readonly ClaimFlowDbContext _db;

    public SqlServerReportingQuery(ClaimFlowDbContext db) => _db = db;

    public Task<IReadOnlyList<DenialRollupRow>> GetDenialRollupAsync(
        DateOnly fromDate,
        DateOnly toDate,
        string? payerId,
        CancellationToken cancellationToken) =>
        ExecuteAsync("dbo.usp_DenialReasonRollup", reader =>
        {
            var rows = new List<DenialRollupRow>();
            while (reader.Read())
            {
                rows.Add(new DenialRollupRow(
                    reader.GetString(reader.GetOrdinal("DenialCode")),
                    reader.GetString(reader.GetOrdinal("Description")),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("ClaimCount"))),
                    Convert.ToInt64(reader.GetValue(reader.GetOrdinal("ChargeCents"))),
                    Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("PercentOfDenials"))),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("Rank")))));
            }

            return rows;
        }, cancellationToken,
            ("@FromDate", fromDate.ToDateTime(TimeOnly.MinValue)),
            ("@ToDate", toDate.ToDateTime(TimeOnly.MinValue)),
            ("@PayerId", string.IsNullOrWhiteSpace(payerId) ? DBNull.Value : payerId.Trim().ToUpperInvariant()));

    public Task<IReadOnlyList<DaysInArRow>> GetDaysInArAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken) =>
        ExecuteAsync("dbo.usp_DaysInAR", reader =>
        {
            var rows = new List<DaysInArRow>();
            while (reader.Read())
            {
                rows.Add(new DaysInArRow(
                    reader.GetString(reader.GetOrdinal("PayerId")),
                    reader.GetString(reader.GetOrdinal("PayerName")),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("OpenClaimCount"))),
                    Convert.ToInt64(reader.GetValue(reader.GetOrdinal("OpenArCents"))),
                    Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("AverageDaysInAr"))),
                    Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("MedianDaysInAr"))),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("Bucket0To30Claims"))),
                    Convert.ToInt64(reader.GetValue(reader.GetOrdinal("Bucket0To30Cents"))),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("Bucket31To60Claims"))),
                    Convert.ToInt64(reader.GetValue(reader.GetOrdinal("Bucket31To60Cents"))),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("Bucket61To90Claims"))),
                    Convert.ToInt64(reader.GetValue(reader.GetOrdinal("Bucket61To90Cents"))),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("Bucket90PlusClaims"))),
                    Convert.ToInt64(reader.GetValue(reader.GetOrdinal("Bucket90PlusCents")))));
            }

            return rows;
        }, cancellationToken,
            ("@AsOfDate", asOfDate.ToDateTime(TimeOnly.MinValue)));

    public Task<IReadOnlyList<ValidationRejectRow>> GetValidationRejectsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken) =>
        ExecuteAsync("dbo.usp_ValidationRejectSummary", reader =>
        {
            var rows = new List<ValidationRejectRow>();
            while (reader.Read())
            {
                rows.Add(new ValidationRejectRow(
                    reader.GetString(reader.GetOrdinal("RuleCode")),
                    reader.GetString(reader.GetOrdinal("Description")),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("RejectCount"))),
                    Convert.ToDecimal(reader.GetValue(reader.GetOrdinal("PercentOfSubmissions")))));
            }

            return rows;
        }, cancellationToken,
            ("@FromDate", fromDate.ToDateTime(TimeOnly.MinValue)),
            ("@ToDate", toDate.ToDateTime(TimeOnly.MinValue)));

    public Task<IReadOnlyList<ThroughputRow>> GetThroughputAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken) =>
        ExecuteAsync("dbo.usp_ClaimThroughputByHour", reader =>
        {
            var rows = new List<ThroughputRow>();
            while (reader.Read())
            {
                rows.Add(new ThroughputRow(
                    reader.GetDateTime(reader.GetOrdinal("HourStartUtc")),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("IngestedCount"))),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("AdjudicatedCount")))));
            }

            return rows;
        }, cancellationToken,
            ("@FromDate", fromDate.ToDateTime(TimeOnly.MinValue)),
            ("@ToDate", toDate.ToDateTime(TimeOnly.MinValue)));

    private async Task<IReadOnlyList<T>> ExecuteAsync<T>(
        string procedure,
        Func<DbDataReader, IReadOnlyList<T>> map,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var connection = _db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await _db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = procedure;
            command.CommandType = CommandType.StoredProcedure;
            command.CommandTimeout = 60;

            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return map(reader);
        }
        finally
        {
            if (openedHere)
            {
                await _db.Database.CloseConnectionAsync();
            }
        }
    }
}
