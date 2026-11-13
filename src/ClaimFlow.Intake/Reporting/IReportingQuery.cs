using ClaimFlow.Shared.Reporting;

namespace ClaimFlow.Intake.Reporting;

/// <summary>
/// Both providers implement this contract. SQL Server executes the db/ stored procedures;
/// SQLite uses an equivalent LINQ implementation for tests and local fallback runs.
/// </summary>
public interface IReportingQuery
{
    Task<IReadOnlyList<DenialRollupRow>> GetDenialRollupAsync(
        DateOnly fromDate,
        DateOnly toDate,
        string? payerId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DaysInArRow>> GetDaysInArAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ValidationRejectRow>> GetValidationRejectsAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ThroughputRow>> GetThroughputAsync(
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);
}
