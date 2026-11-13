using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimFlow.Data;
using ClaimFlow.Data.Entities;
using ClaimFlow.Intake.Contracts;
using ClaimFlow.Intake.Infrastructure;
using ClaimFlow.Intake.Services;
using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;
using ClaimFlow.Shared.Messaging;
using ClaimFlow.Shared.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Intake.Controllers;

[ApiExplorerSettings(GroupName = "claims")]
[ApiController]
[Route("api/claims")]
[Route("api/v1/claims")]
public sealed class ClaimsController : ControllerBase
{
    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ClaimIngestService _ingest;
    private readonly ClaimFlowDbContext _db;
    private readonly TimeProvider _timeProvider;

    public ClaimsController(ClaimIngestService ingest, ClaimFlowDbContext db, TimeProvider timeProvider)
    {
        _ingest = ingest;
        _db = db;
        _timeProvider = timeProvider;
    }

    private DateOnly Today => DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

    [HttpPost]
    [ProducesResponseType(typeof(ClaimDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] ClaimBundle bundle, CancellationToken cancellationToken)
    {
        var outcome = await _ingest.IngestAsync(bundle, HttpContext.GetCorrelationId(), Today, cancellationToken);
        return outcome.Kind switch
        {
            IngestKind.Accepted => CreatedAtAction(
                nameof(GetById),
                new { claimId = outcome.Claim!.ClaimId },
                MapDetail(outcome.Claim)),
            IngestKind.Rejected => RejectedProblem(outcome),
            _ => DuplicateProblem(outcome),
        };
    }

    [HttpPost("batch")]
    [RequestSizeLimit(60_000_000)]
    [Consumes("application/json", "application/x-ndjson", "text/plain")]
    [ProducesResponseType(typeof(BatchIngestResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateBatch(CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: false))
        {
            body = await reader.ReadToEndAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Empty payload",
                detail: "Send an NDJSON stream or a JSON array of claim bundles.");
        }

        IReadOnlyList<ClaimBundle> bundles;
        try
        {
            bundles = ParseBundles(body);
        }
        catch (JsonException ex)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid payload",
                detail: ex.Message);
        }

        var correlationId = HttpContext.GetCorrelationId();
        var results = new List<BatchItemResult>(bundles.Count);
        var accepted = 0;
        var rejected = 0;
        var duplicates = 0;

        for (var index = 0; index < bundles.Count; index++)
        {
            var outcome = await _ingest.IngestAsync(bundles[index], correlationId, Today, cancellationToken);
            _db.ChangeTracker.Clear();

            switch (outcome.Kind)
            {
                case IngestKind.Accepted:
                    accepted++;
                    results.Add(new BatchItemResult(index, outcome.Claim!.ClaimId, "Accepted",
                        Array.Empty<string>(), Array.Empty<ValidationIssue>()));
                    break;
                case IngestKind.Rejected:
                    rejected++;
                    results.Add(new BatchItemResult(index, outcome.Claim!.ClaimId, "Rejected",
                        outcome.Errors.Select(e => e.RuleCode).Distinct().ToArray(), outcome.Errors));
                    break;
                default:
                    duplicates++;
                    results.Add(new BatchItemResult(index, bundles[index].Claim.ClaimId, "Duplicate",
                        new[] { DenialReasons.Code(DenialReasonCode.CO18) }, Array.Empty<ValidationIssue>()));
                    break;
            }
        }

        return Ok(new BatchIngestResponse(bundles.Count, accepted, rejected, duplicates, results));
    }

    [HttpGet("{claimId}")]
    [ProducesResponseType(typeof(ClaimDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(string claimId, CancellationToken cancellationToken)
    {
        var claim = await _db.Claims
            .AsNoTracking()
            .Include(c => c.Lines)
            .Include(c => c.StatusHistory)
            .SingleOrDefaultAsync(c => c.ClaimId == claimId, cancellationToken);

        return claim is null ? ClaimNotFound(claimId) : Ok(MapDetail(claim));
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ClaimSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] string? payer,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        ClaimStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<ClaimStatus>(status, ignoreCase: true, out var parsed))
            {
                return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid status filter",
                    detail: $"'{status}' is not a valid claim status.");
            }

            statusFilter = parsed;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _db.Claims.AsNoTracking();
        if (statusFilter is not null)
        {
            query = query.Where(c => c.Status == statusFilter);
        }

        if (!string.IsNullOrWhiteSpace(payer))
        {
            var payerId = payer.Trim().ToUpperInvariant();
            query = query.Where(c => c.PayerId == payerId);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(c => c.ReceivedAtUnixMs)
            .ThenBy(c => c.ClaimId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new ClaimSummaryDto(
                c.Id,
                c.ClaimId,
                c.PayerId,
                c.MemberId,
                c.ServiceDate,
                c.TotalChargeCents,
                c.Status,
                c.Outcome,
                c.DenialReasonCode,
                c.PaidCents,
                c.PatientResponsibilityCents,
                c.ReceivedAt))
            .ToListAsync(cancellationToken);

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize);
        return Ok(new PagedResult<ClaimSummaryDto>(items, page, pageSize, total, totalPages));
    }

    [HttpPut("{claimId}/status")]
    [ProducesResponseType(typeof(ClaimDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateStatus(
        string claimId,
        [FromBody] ClaimStatusUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Status == ClaimStatus.Received)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid status transition",
                detail: "A claim cannot be moved back to Received.");
        }

        if (request.AllowedCents < 0 || request.PaidCents < 0 || request.PatientResponsibilityCents < 0)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid financial amounts",
                detail: "Allowed, paid and patient-responsibility amounts must be non-negative integer cents.");
        }

        var claim = await _db.Claims
            .Include(c => c.Lines)
            .Include(c => c.StatusHistory)
            .SingleOrDefaultAsync(c => c.ClaimId == claimId, cancellationToken);

        if (claim is null)
        {
            return ClaimNotFound(claimId);
        }

        if (request.EventId is { } eventId &&
            claim.StatusHistory.Any(h => h.EventId == eventId))
        {
            return Ok(MapDetail(claim));
        }

        var now = DateTimeOffset.UtcNow;
        var sequence = claim.StatusHistory.Count == 0 ? 1 : claim.StatusHistory.Max(h => h.Sequence) + 1;
        var actor = string.IsNullOrWhiteSpace(request.Actor) ? "adjudication" : request.Actor.Trim();
        var correlationId = HttpContext.GetCorrelationId();

        if (request.Status is ClaimStatus.Paid or ClaimStatus.Denied &&
            claim.StatusHistory.All(h => h.Status != ClaimStatus.Adjudicated))
        {
            claim.StatusHistory.Add(new ClaimStatusHistory
            {
                ClaimId = claim.Id,
                Status = ClaimStatus.Adjudicated,
                Sequence = sequence++,
                ChangedAt = now,
                Reason = "Adjudication rules completed",
                Actor = actor,
                CorrelationId = correlationId,
            });
            _db.Entry(claim.StatusHistory[^1]).State = EntityState.Added;
        }

        claim.Status = request.Status;
        claim.Outcome = request.Outcome ??
            (request.Status == ClaimStatus.Denied ? AdjudicationOutcome.Denied
                : request.Status == ClaimStatus.Paid ? AdjudicationOutcome.Paid
                : null);
        claim.DenialReasonCode = request.DenialReasonCode;
        claim.AllowedCents = request.AllowedCents;
        claim.PaidCents = request.PaidCents;
        claim.PatientResponsibilityCents = request.PatientResponsibilityCents;
        claim.LastStatusChangeAt = now;
        if (request.Status is ClaimStatus.Paid or ClaimStatus.Denied)
        {
            claim.AdjudicatedAt = now;
        }

        AllocateLineAmounts(claim);

        claim.StatusHistory.Add(new ClaimStatusHistory
        {
            ClaimId = claim.Id,
            Status = request.Status,
            Sequence = sequence,
            ChangedAt = now,
            Reason = request.Reason ?? $"Status set to {request.Status}",
            Actor = actor,
            CorrelationId = correlationId,
            EventId = request.EventId,
        });
        _db.Entry(claim.StatusHistory[^1]).State = EntityState.Added;

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(MapDetail(claim));
    }

    private static void AllocateLineAmounts(Claim claim)
    {
        if (claim.Lines.Count == 0)
        {
            return;
        }

        var totalCharge = (long)claim.Lines.Sum(l => Math.Max(0, l.ChargeCents));
        if (totalCharge <= 0)
        {
            return;
        }

        var ordered = claim.Lines.OrderBy(l => l.LineNumber).ToArray();
        var allocatedAllowed = 0;
        var allocatedPaid = 0;
        var allocatedPatient = 0;

        for (var i = 0; i < ordered.Length; i++)
        {
            var isLast = i == ordered.Length - 1;
            var share = Math.Max(0, ordered[i].ChargeCents) / (double)totalCharge;
            var allowed = isLast ? claim.AllowedCents - allocatedAllowed : (int)Math.Round(claim.AllowedCents * share);
            var paid = isLast ? claim.PaidCents - allocatedPaid : (int)Math.Round(claim.PaidCents * share);
            var patient = isLast
                ? claim.PatientResponsibilityCents - allocatedPatient
                : (int)Math.Round(claim.PatientResponsibilityCents * share);

            ordered[i].AllowedCents = allowed;
            ordered[i].PaidCents = paid;
            ordered[i].PatientResponsibilityCents = patient;
            allocatedAllowed += allowed;
            allocatedPaid += paid;
            allocatedPatient += patient;
        }
    }

    private static IReadOnlyList<ClaimBundle> ParseBundles(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('['))
        {
            return JsonSerializer.Deserialize<List<ClaimBundle>>(trimmed, ParseOptions) ?? new List<ClaimBundle>();
        }

        var bundles = new List<ClaimBundle>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bundle = JsonSerializer.Deserialize<ClaimBundle>(line, ParseOptions);
            if (bundle is not null)
            {
                bundles.Add(bundle);
            }
        }

        return bundles;
    }

    private ObjectResult RejectedProblem(IngestOutcome outcome)
    {
        var errors = outcome.Errors;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status422UnprocessableEntity,
            Title = "Claim rejected by validation",
            Detail = $"The claim failed {errors.Count} validation rule(s) and was persisted with status Rejected.",
            Type = "https://claimflow.local/problems/claim-validation-failed",
        };
        problem.Extensions["claimId"] = outcome.Claim?.ClaimId;
        problem.Extensions["ruleCodes"] = errors.Select(e => e.RuleCode).Distinct().ToArray();
        problem.Extensions["errors"] = errors;
        return new ObjectResult(problem) { StatusCode = problem.Status };
    }

    private ObjectResult DuplicateProblem(IngestOutcome outcome)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Duplicate claim",
            Detail = "A non-rejected claim already exists for the same payer, member, service date and total charge.",
            Type = "https://claimflow.local/problems/duplicate-claim",
        };
        problem.Extensions["claimId"] = outcome.Claim?.ClaimId;
        problem.Extensions["ruleCodes"] = new[] { DenialReasons.Code(DenialReasonCode.CO18) };
        return new ObjectResult(problem) { StatusCode = problem.Status };
    }

    private ObjectResult ClaimNotFound(string claimId) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Claim not found", detail: $"No claim with id '{claimId}'.");

    private static ClaimDetailDto MapDetail(Claim claim) => new(
        claim.Id,
        claim.ClaimId,
        claim.PayerId,
        claim.MemberId,
        claim.ProviderNpi,
        claim.PlaceOfService,
        claim.ServiceDate,
        claim.TotalChargeCents,
        claim.Status,
        claim.Outcome,
        claim.DenialReasonCode,
        claim.AllowedCents,
        claim.PaidCents,
        claim.PatientResponsibilityCents,
        claim.ValidationRuleCodes,
        claim.CorrelationId,
        claim.ReceivedAt,
        claim.AdjudicatedAt,
        claim.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new ClaimLineDto(l.LineNumber, l.CptCode, l.Icd10Code, l.PlaceOfService,
                l.ChargeCents, l.Units, l.AllowedCents, l.PaidCents, l.PatientResponsibilityCents))
            .ToArray(),
        claim.StatusHistory
            .OrderBy(h => h.Sequence)
            .Select(h => new ClaimStatusHistoryDto(h.Status, h.ChangedAt, h.Reason, h.Actor))
            .ToArray());
}
