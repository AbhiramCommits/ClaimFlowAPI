using ClaimFlow.Adjudication.Contracts;
using ClaimFlow.Adjudication.Services;
using ClaimFlow.Security.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClaimFlow.Adjudication.Controllers;

[ApiExplorerSettings(GroupName = "adjudication")]
[Authorize(Policy = ClaimFlowPolicies.Adjudicate)]
[ApiController]
[Route("api/v1/adjudication")]
public sealed class AdjudicationController : ControllerBase
{
    private readonly AdjudicationEngine _engine;
    private readonly IEligibilityService _eligibility;
    private readonly TimeProvider _timeProvider;

    public AdjudicationController(
        AdjudicationEngine engine,
        IEligibilityService eligibility,
        TimeProvider timeProvider)
    {
        _engine = engine;
        _eligibility = eligibility;
        _timeProvider = timeProvider;
    }

    [HttpPost("dry-run")]
    [ProducesResponseType(typeof(DryRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public ActionResult<DryRunResponse> DryRun([FromBody] DryRunRequest request)
    {
        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid dry-run request",
                detail: "At least one service line is required.");
        }

        var claimId = string.IsNullOrWhiteSpace(request.ClaimId)
            ? $"DRYRUN-{Guid.NewGuid():N}"
            : request.ClaimId;

        var eligibility = _eligibility.Check(new EligibilityRequest(
            request.MemberId,
            request.PayerId,
            request.ServiceDate));

        var result = _engine.Adjudicate(
            new AdjudicationRequest(
                claimId,
                request.PayerId,
                request.MemberId,
                request.ServiceDate,
                request.Lines
                    .Select((line, index) => new AdjudicationLineRequest(
                        index + 1,
                        line.CptCode?.Trim().ToUpperInvariant() ?? string.Empty,
                        line.Icd10Code?.Trim().ToUpperInvariant() ?? string.Empty,
                        line.ChargeCents))
                    .ToArray(),
                request.DiagnosisCodes ?? Array.Empty<string>()),
            eligibility,
            DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime));

        return Ok(new DryRunResponse(
            claimId,
            result.Outcome,
            result.DenialReasonCode,
            result.AllowedCents,
            result.PaidCents,
            result.PatientResponsibilityCents,
            result.Lines,
            result.AppliedRules,
            new EligibilityCheckResponse(
                eligibility.Eligible,
                eligibility.Status,
                eligibility.DenialReasonCode,
                eligibility.DeductibleRemainingCents,
                eligibility.TerminationDate,
                eligibility.Message)));
    }
}
