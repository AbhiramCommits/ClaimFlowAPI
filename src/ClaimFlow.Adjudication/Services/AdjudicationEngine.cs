using ClaimFlow.Shared;

namespace ClaimFlow.Adjudication.Services;

public sealed record AdjudicationLineRequest(int LineNumber, string CptCode, string Icd10Code, int ChargeCents);

public sealed record AdjudicationRequest(
    string ClaimId,
    string PayerId,
    string MemberId,
    DateOnly ServiceDate,
    IReadOnlyList<AdjudicationLineRequest> Lines,
    IReadOnlyList<string> DiagnosisCodes);

public sealed record AdjudicationLineResult(
    int LineNumber,
    string CptCode,
    int ChargeCents,
    int AllowedCents,
    int PaidCents,
    int PatientResponsibilityCents,
    string? AdjustmentReason);

public sealed record AdjudicationResult(
    AdjudicationOutcome Outcome,
    DenialReasonCode? DenialReasonCode,
    int AllowedCents,
    int PaidCents,
    int PatientResponsibilityCents,
    IReadOnlyList<AdjudicationLineResult> Lines,
    IReadOnlyList<string> AppliedRules);

/// <summary>
/// Deterministic adjudication rules: eligibility, timely filing, medical necessity, bundling,
/// non-covered benefits, deductible/coinsurance, and contractual fee-schedule adjustment.
/// </summary>
public sealed class AdjudicationEngine
{
    private static readonly Dictionary<string, int> EmergencyAndEvaluationLevels = new(StringComparer.Ordinal)
    {
        ["99213"] = 1,
        ["99214"] = 2,
        ["99215"] = 3,
    };

    private static readonly Dictionary<string, string[]> MedicalNecessityDiagnoses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["27447"] = new[] { "M17" },
    };

    private static readonly Dictionary<string, HashSet<string>> NonCoveredProcedures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PAYER-AETNA"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "20610" },
        ["PAYER-BCBS"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "12002" },
        ["PAYER-CIGNA"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99396" },
        ["PAYER-HUMANA"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "90471" },
        ["PAYER-MEDICARE"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "99396" },
        ["PAYER-UHC"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "45378" },
    };

    public AdjudicationResult Adjudicate(AdjudicationRequest request, EligibilityResult eligibility, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eligibility);

        if (!eligibility.Eligible)
        {
            return HardDenial(request, eligibility.DenialReasonCode ?? DenialReasonCode.CO27,
                "COVERAGE_TERMINATED_CO27");
        }

        if (!PayerDirectory.TryGet(request.PayerId, out var payer))
        {
            return HardDenial(request, DenialReasonCode.CO109, "PAYER_NOT_CONTRACTED_CO109");
        }

        if (request.ServiceDate < today.AddDays(-payer.FilingLimitDays))
        {
            return HardDenial(request, DenialReasonCode.CO29, "TIMELY_FILING_CO29");
        }

        if (!MeetsMedicalNecessity(request))
        {
            return HardDenial(request, DenialReasonCode.CO50, "MEDICAL_NECESSITY_CO50");
        }

        var bundledLineNumbers = FindBundledLines(request.Lines);
        var deductibleRemaining = eligibility.DeductibleRemainingCents;
        var coinsurancePercent = 100 - payer.CoveragePercent;
        var rows = new List<AdjudicationLineResult>(request.Lines.Count);
        var rules = new List<string> { "ELIGIBLE" };
        var anyDeductible = false;
        var anyBundled = false;
        var anyNonCovered = false;

        foreach (var line in request.Lines.OrderBy(l => l.LineNumber))
        {
            var contractedAllowed = (int)Math.Round(
                line.ChargeCents * payer.FeeSchedulePercent / 100.0,
                MidpointRounding.AwayFromZero);

            if (bundledLineNumbers.Contains(line.LineNumber))
            {
                anyBundled = true;
                rows.Add(new AdjudicationLineResult(line.LineNumber, line.CptCode, line.ChargeCents,
                    0, 0, 0, "CO97:BUNDLED"));
                continue;
            }

            if (IsNonCovered(payer.PayerId, line.CptCode))
            {
                anyNonCovered = true;
                rows.Add(new AdjudicationLineResult(line.LineNumber, line.CptCode, line.ChargeCents,
                    0, 0, line.ChargeCents, "PR204:NON_COVERED"));
                continue;
            }

            var deductiblePortion = Math.Min(deductibleRemaining, contractedAllowed);
            deductibleRemaining -= deductiblePortion;
            if (deductiblePortion > 0)
            {
                anyDeductible = true;
            }

            var afterDeductible = contractedAllowed - deductiblePortion;
            var coinsurance = (int)Math.Round(
                afterDeductible * coinsurancePercent / 100.0,
                MidpointRounding.AwayFromZero);
            var paid = afterDeductible - coinsurance;
            var patient = deductiblePortion + coinsurance;

            var adjustment = deductiblePortion > 0 ? "PR1:DEDUCTIBLE" : "CONTRACTUAL";
            rows.Add(new AdjudicationLineResult(line.LineNumber, line.CptCode, line.ChargeCents,
                contractedAllowed, paid, patient, adjustment));
        }

        var allowed = rows.Sum(r => r.AllowedCents);
        var paidTotal = rows.Sum(r => r.PaidCents);
        var patientTotal = rows.Sum(r => r.PatientResponsibilityCents);

        if (anyNonCovered)
        {
            rules.Add("NON_COVERED_PR204");
        }

        if (anyBundled)
        {
            rules.Add("BUNDLED_CO97");
        }

        if (anyDeductible)
        {
            rules.Add("DEDUCTIBLE_PR1");
        }

        if (rows.Any(r => r.AdjustmentReason == "CONTRACTUAL"))
        {
            rules.Add("FEE_SCHEDULE");
        }

        var denialCode = anyNonCovered
            ? DenialReasonCode.PR204
            : anyBundled
                ? DenialReasonCode.CO97
                : anyDeductible
                    ? DenialReasonCode.PR1
                    : (DenialReasonCode?)null;

        AdjudicationOutcome outcome;
        if (paidTotal > 0)
        {
            outcome = denialCode is null && patientTotal == 0
                ? AdjudicationOutcome.Paid
                : AdjudicationOutcome.PartiallyPaid;
        }
        else if (patientTotal > 0)
        {
            outcome = AdjudicationOutcome.PartiallyPaid;
        }
        else
        {
            outcome = AdjudicationOutcome.Denied;
            denialCode ??= DenialReasonCode.CO16;
        }

        return new AdjudicationResult(outcome, denialCode, allowed, paidTotal, patientTotal, rows, rules);
    }

    private static AdjudicationResult HardDenial(
        AdjudicationRequest request,
        DenialReasonCode code,
        string appliedRule)
    {
        var lines = request.Lines
            .OrderBy(l => l.LineNumber)
            .Select(l => new AdjudicationLineResult(l.LineNumber, l.CptCode, l.ChargeCents, 0, 0, 0,
                $"{DenialReasons.Code(code)}:DENIED"))
            .ToArray();
        return new AdjudicationResult(AdjudicationOutcome.Denied, code, 0, 0, 0, lines, new[] { appliedRule });
    }

    private static bool MeetsMedicalNecessity(AdjudicationRequest request)
    {
        var diagnoses = request.DiagnosisCodes
            .Concat(request.Lines.Select(l => l.Icd10Code))
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var line in request.Lines)
        {
            if (!MedicalNecessityDiagnoses.TryGetValue(line.CptCode, out var requiredPrefixes))
            {
                continue;
            }

            var supported = diagnoses.Any(diagnosis =>
                requiredPrefixes.Any(prefix => diagnosis.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
            if (!supported)
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<int> FindBundledLines(IReadOnlyList<AdjudicationLineRequest> lines)
    {
        var evaluationLines = lines
            .Where(l => EmergencyAndEvaluationLevels.ContainsKey(l.CptCode))
            .ToArray();
        if (evaluationLines.Length < 2)
        {
            return new HashSet<int>();
        }

        var highestLevel = evaluationLines.Max(l => EmergencyAndEvaluationLevels[l.CptCode]);
        return evaluationLines
            .Where(l => EmergencyAndEvaluationLevels[l.CptCode] < highestLevel)
            .Select(l => l.LineNumber)
            .ToHashSet();
    }

    private static bool IsNonCovered(string payerId, string cptCode) =>
        NonCoveredProcedures.TryGetValue(payerId, out var codes) && codes.Contains(cptCode);
}
