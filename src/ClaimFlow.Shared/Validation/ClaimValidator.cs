using System.Globalization;
using System.Text.RegularExpressions;

namespace ClaimFlow.Shared.Validation;

public static class ValidationRuleCodes
{
    public const string MissingMemberId = "MBR001";
    public const string UnknownPayer = "PAY001";
    public const string FutureServiceDate = "SVC001";
    public const string FilingLimitExceeded = "SVC002";
    public const string MissingServiceDate = "SVC003";
    public const string InvalidNpi = "NPI001";
    public const string InvalidIcd10 = "ICD001";
    public const string InvalidCpt = "CPT001";
    public const string NonPositiveCharge = "CHG001";
    public const string LineTotalMismatch = "CHG002";
    public const string DuplicateLine = "DUP001";
    public const string MissingPlaceOfService = "POS001";

    public static IReadOnlyDictionary<string, string> Descriptions { get; } = new Dictionary<string, string>
    {
        [MissingMemberId] = "Member identifier is missing",
        [UnknownPayer] = "Payer is not in the contracted payer directory",
        [FutureServiceDate] = "Service date is in the future",
        [FilingLimitExceeded] = "Service date is older than the payer filing limit",
        [MissingServiceDate] = "Service date is missing",
        [InvalidNpi] = "Provider NPI failed format or Luhn checksum validation",
        [InvalidIcd10] = "Diagnosis code is not a valid ICD-10-CM code",
        [InvalidCpt] = "Procedure code is not a valid CPT/HCPCS code",
        [NonPositiveCharge] = "Line or claim charge is zero or negative",
        [LineTotalMismatch] = "Line charges do not sum to the claim total",
        [DuplicateLine] = "Duplicate service line on the same claim",
        [MissingPlaceOfService] = "Place of service is missing or invalid",
    };
}

public sealed record ValidationIssue(string RuleCode, string Field, string Message);

public sealed class ValidationResult
{
    public static ValidationResult Success { get; } = new(Array.Empty<ValidationIssue>());

    public ValidationResult(IReadOnlyList<ValidationIssue> errors) => Errors = errors;

    public IReadOnlyList<ValidationIssue> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<string> RuleCodes => Errors.Select(e => e.RuleCode).Distinct().ToArray();
}

/// <summary>
/// Pure, deterministic claim validation. No I/O, no clock reads beyond the injected <paramref name="today"/>.
/// </summary>
public static class ClaimValidator
{
    private static readonly Regex Icd10Pattern = new(
        @"^[A-Z][0-9][0-9A-Z](\.[0-9A-Z]{1,4})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CptPattern = new(
        @"^[0-9]{5}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlaceOfServicePattern = new(
        @"^[0-9]{2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ValidationResult Validate(Dtos.ClaimBundle bundle, DateOnly? today = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var issues = new List<ValidationIssue>();
        var seen = new HashSet<(string Code, string Field)>();
        var asOf = today ?? DateOnly.FromDateTime(DateTime.UtcNow);

        void Add(string ruleCode, string field, string message)
        {
            if (seen.Add((ruleCode, field)))
            {
                issues.Add(new ValidationIssue(ruleCode, field, message));
            }
        }

        if (string.IsNullOrWhiteSpace(bundle.MemberId) ||
            string.IsNullOrWhiteSpace(bundle.Patient.MemberId))
        {
            Add(ValidationRuleCodes.MissingMemberId, "memberId", "Member identifier is required.");
        }

        Domain.PayerProfile? payer = null;
        if (string.IsNullOrWhiteSpace(bundle.PayerId) ||
            !PayerDirectory.TryGet(bundle.PayerId, out var knownPayer))
        {
            Add(ValidationRuleCodes.UnknownPayer, "payerId", $"Payer '{bundle.PayerId}' is not recognized.");
        }
        else
        {
            payer = knownPayer;
        }

        var serviceDate = bundle.Claim.ServiceDate;
        if (serviceDate == default)
        {
            Add(ValidationRuleCodes.MissingServiceDate, "claim.serviceDate", "Service date is required.");
        }
        else
        {
            if (serviceDate > asOf)
            {
                Add(ValidationRuleCodes.FutureServiceDate, "claim.serviceDate",
                    $"Service date {serviceDate:yyyy-MM-dd} is after today ({asOf:yyyy-MM-dd}).");
            }

            if (payer is not null && serviceDate < asOf.AddDays(-payer.FilingLimitDays))
            {
                Add(ValidationRuleCodes.FilingLimitExceeded, "claim.serviceDate",
                    $"Service date {serviceDate:yyyy-MM-dd} exceeds the {payer.Name} filing limit of {payer.FilingLimitDays} days.");
            }
        }

        if (!NpiValidator.IsValid(bundle.Claim.ProviderNpi))
        {
            Add(ValidationRuleCodes.InvalidNpi, "claim.providerNpi",
                $"Provider NPI '{bundle.Claim.ProviderNpi}' is not a valid 10-digit NPI.");
        }

        if (IsInvalidPlaceOfService(bundle.Claim.PlaceOfService))
        {
            Add(ValidationRuleCodes.MissingPlaceOfService, "claim.placeOfService",
                $"Place of service '{bundle.Claim.PlaceOfService}' is missing or invalid.");
        }

        var lines = bundle.Claim.Lines ?? Array.Empty<Dtos.ClaimLineItem>();
        if (lines.Count == 0)
        {
            Add(ValidationRuleCodes.LineTotalMismatch, "claim.lines", "Claim has no service lines.");
        }

        var lineSum = 0L;
        foreach (var line in lines)
        {
            var lineNumber = line.LineNumber.ToString(CultureInfo.InvariantCulture);

            if (!CptPattern.IsMatch((line.CptCode ?? string.Empty).Trim()))
            {
                Add(ValidationRuleCodes.InvalidCpt, $"lines[{lineNumber}].cptCode",
                    $"CPT code '{line.CptCode}' is not valid.");
            }

            if (!string.IsNullOrWhiteSpace(line.Icd10Code) && !Icd10Pattern.IsMatch(line.Icd10Code.Trim()))
            {
                Add(ValidationRuleCodes.InvalidIcd10, $"lines[{lineNumber}].icd10Code",
                    $"ICD-10 code '{line.Icd10Code}' is not valid.");
            }

            if (IsInvalidPlaceOfService(line.PlaceOfService))
            {
                Add(ValidationRuleCodes.MissingPlaceOfService, $"lines[{lineNumber}].placeOfService",
                    $"Place of service '{line.PlaceOfService}' is missing or invalid.");
            }

            if (line.ChargeCents <= 0)
            {
                Add(ValidationRuleCodes.NonPositiveCharge, $"lines[{lineNumber}].chargeCents",
                    $"Line charge must be positive but was {line.ChargeCents} cents.");
            }

            if (line.Units <= 0)
            {
                Add(ValidationRuleCodes.NonPositiveCharge, $"lines[{lineNumber}].units",
                    $"Line units must be positive but were {line.Units}.");
            }

            lineSum += line.ChargeCents;
        }

        if (bundle.Claim.TotalChargeCents <= 0)
        {
            Add(ValidationRuleCodes.NonPositiveCharge, "claim.totalChargeCents",
                $"Claim total must be positive but was {bundle.Claim.TotalChargeCents} cents.");
        }
        else if (lines.Count > 0 && lineSum != bundle.Claim.TotalChargeCents)
        {
            Add(ValidationRuleCodes.LineTotalMismatch, "claim.totalChargeCents",
                $"Line charges sum to {lineSum} cents but the claim total is {bundle.Claim.TotalChargeCents} cents.");
        }

        var duplicate = lines
            .Where(l => !string.IsNullOrWhiteSpace(l.CptCode))
            .GroupBy(l => (Cpt: l.CptCode.Trim().ToUpperInvariant(), l.ChargeCents))
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            Add(ValidationRuleCodes.DuplicateLine, "claim.lines",
                $"CPT {duplicate.Key.Cpt} appears {duplicate.Count()} times with the same charge.");
        }

        foreach (var diagnosis in bundle.Claim.DiagnosisCodes ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(diagnosis) && !Icd10Pattern.IsMatch(diagnosis.Trim()))
            {
                Add(ValidationRuleCodes.InvalidIcd10, "claim.diagnosisCodes",
                    $"ICD-10 code '{diagnosis}' is not valid.");
            }
        }

        return issues.Count == 0 ? ValidationResult.Success : new ValidationResult(issues);
    }

    private static bool IsInvalidPlaceOfService(string? placeOfService) =>
        string.IsNullOrWhiteSpace(placeOfService) ||
        !PlaceOfServicePattern.IsMatch(placeOfService.Trim());
}
