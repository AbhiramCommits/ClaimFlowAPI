using ClaimFlow.Adjudication.Contracts;
using ClaimFlow.Adjudication.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaimFlow.Adjudication.Controllers;

[ApiExplorerSettings(GroupName = "adjudication")]
[ApiController]
[Route("api/v1/eligibility")]
public sealed class EligibilityController : ControllerBase
{
    private readonly IEligibilityService _eligibility;

    public EligibilityController(IEligibilityService eligibility) => _eligibility = eligibility;

    [HttpPost("check")]
    [ProducesResponseType(typeof(EligibilityCheckResponse), StatusCodes.Status200OK)]
    public ActionResult<EligibilityCheckResponse> Check([FromBody] EligibilityCheckRequest request)
    {
        var result = _eligibility.Check(new EligibilityRequest(request.MemberId, request.PayerId, request.ServiceDate));
        return Ok(new EligibilityCheckResponse(
            result.Eligible,
            result.Status,
            result.DenialReasonCode,
            result.DeductibleRemainingCents,
            result.TerminationDate,
            result.Message));
    }
}
