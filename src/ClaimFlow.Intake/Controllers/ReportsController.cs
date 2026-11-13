using ClaimFlow.Intake.Reporting;
using ClaimFlow.Security.Auth;
using ClaimFlow.Shared.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimFlow.Intake.Controllers;

[ApiExplorerSettings(GroupName = "reports")]
[Authorize(Policy = ClaimFlowPolicies.ReportingRead)]
[ApiController]
[Route("api/v1/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportingQuery _reporting;
    private readonly TimeProvider _timeProvider;

    public ReportsController(IReportingQuery reporting, TimeProvider timeProvider)
    {
        _reporting = reporting;
        _timeProvider = timeProvider;
    }

    [HttpGet("denials")]
    [ProducesResponseType(typeof(IReadOnlyList<DenialRollupRow>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DenialRollupRow>>> Denials(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] string? payerId,
        CancellationToken cancellationToken)
    {
        var (from, to) = ResolveRange(fromDate, toDate);
        var rows = await _reporting.GetDenialRollupAsync(from, to, payerId, cancellationToken);
        return Ok(rows);
    }

    [HttpGet("days-in-ar")]
    [ProducesResponseType(typeof(IReadOnlyList<DaysInArRow>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DaysInArRow>>> DaysInAr(
        [FromQuery] DateOnly? asOfDate,
        CancellationToken cancellationToken)
    {
        var asOf = asOfDate ?? Today;
        var rows = await _reporting.GetDaysInArAsync(asOf, cancellationToken);
        return Ok(rows);
    }

    [HttpGet("rejects")]
    [ProducesResponseType(typeof(IReadOnlyList<ValidationRejectRow>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ValidationRejectRow>>> Rejects(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        var (from, to) = ResolveRange(fromDate, toDate);
        var rows = await _reporting.GetValidationRejectsAsync(from, to, cancellationToken);
        return Ok(rows);
    }

    [HttpGet("throughput")]
    [ProducesResponseType(typeof(IReadOnlyList<ThroughputRow>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ThroughputRow>>> Throughput(
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        CancellationToken cancellationToken)
    {
        var (from, to) = ResolveRange(fromDate, toDate);
        var rows = await _reporting.GetThroughputAsync(from, to, cancellationToken);
        return Ok(rows);
    }

    private DateOnly Today => DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

    private (DateOnly From, DateOnly To) ResolveRange(DateOnly? fromDate, DateOnly? toDate)
    {
        var to = toDate ?? Today;
        var from = fromDate ?? to.AddDays(-30);
        if (from > to)
        {
            (from, to) = (to, from);
        }

        return (from, to);
    }
}
