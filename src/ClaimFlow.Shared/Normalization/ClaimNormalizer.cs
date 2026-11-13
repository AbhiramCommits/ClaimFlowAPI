using System.Security.Cryptography;
using System.Text;
using ClaimFlow.Shared.Domain;
using ClaimFlow.Shared.Dtos;

namespace ClaimFlow.Shared.Normalization;

/// <summary>
/// Maps a validated FHIR-shaped bundle to canonical domain records. Codes are trimmed and upcased,
/// money is always integer cents, and SSNs are reduced to last-4 plus a SHA-256 hash.
/// </summary>
public static class ClaimNormalizer
{
    public static NormalizationResult Normalize(ClaimBundle bundle, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        var payer = PayerDirectory.GetRequired(bundle.PayerId);
        var lines = (bundle.Claim.Lines ?? Array.Empty<ClaimLineItem>())
            .Select(line => new NormalizedClaimLine(
                line.LineNumber,
                Code(line.CptCode),
                Code(line.Icd10Code),
                Code(line.PlaceOfService),
                line.ChargeCents,
                line.Units <= 0 ? 1 : line.Units))
            .OrderBy(l => l.LineNumber)
            .ToArray();

        var totalChargeCents = lines.Sum(l => l.ChargeCents);
        var diagnosisCodes = (bundle.Claim.DiagnosisCodes ?? Array.Empty<string>())
            .Select(Code)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var claim = new NormalizedClaim(
            ClaimId: string.IsNullOrWhiteSpace(bundle.Claim.ClaimId) ? bundle.Id : bundle.Claim.ClaimId.Trim(),
            PayerId: payer.PayerId,
            MemberId: bundle.MemberId.Trim().ToUpperInvariant(),
            ProviderNpi: bundle.Claim.ProviderNpi.Trim(),
            PlaceOfService: Code(bundle.Claim.PlaceOfService),
            ServiceDate: bundle.Claim.ServiceDate,
            TotalChargeCents: totalChargeCents,
            Lines: lines,
            DiagnosisCodes: diagnosisCodes,
            CorrelationId: correlationId ?? string.Empty);

        var digitsOnlySsn = new string((bundle.Patient.Ssn ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        var ssnLast4 = digitsOnlySsn.Length >= 4 ? digitsOnlySsn[^4..] : digitsOnlySsn;
        var ssnHash = HashSsn(digitsOnlySsn);

        var patient = new NormalizedPatient(
            MemberId: bundle.MemberId.Trim().ToUpperInvariant(),
            GivenName: bundle.Patient.GivenName?.Trim() ?? string.Empty,
            FamilyName: bundle.Patient.FamilyName?.Trim() ?? string.Empty,
            DateOfBirth: bundle.Patient.DateOfBirth,
            SsnLast4: ssnLast4,
            SsnHash: ssnHash,
            Address: new NormalizedAddress(
                bundle.Patient.Address?.Line1?.Trim() ?? string.Empty,
                bundle.Patient.Address?.City?.Trim() ?? string.Empty,
                Code(bundle.Patient.Address?.State),
                bundle.Patient.Address?.PostalCode?.Trim() ?? string.Empty));

        var normalizedPayer = new NormalizedPayer(payer.PayerId, payer.Name, payer.FilingLimitDays);

        var coverage = new NormalizedCoverage(
            CoverageId: string.IsNullOrWhiteSpace(bundle.Coverage.CoverageId)
                ? $"COV-{claim.ClaimId}"
                : bundle.Coverage.CoverageId.Trim().ToUpperInvariant(),
            PayerId: payer.PayerId,
            Status: bundle.Coverage.Status,
            EffectiveDate: bundle.Coverage.EffectiveDate,
            TerminationDate: bundle.Coverage.TerminationDate,
            DeductibleRemainingCents: Math.Max(0, bundle.Coverage.DeductibleRemainingCents));

        return new NormalizationResult(claim, patient, normalizedPayer, coverage);
    }

    private static string Code(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string HashSsn(string digitsOnlySsn)
    {
        if (digitsOnlySsn.Length == 0)
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(digitsOnlySsn));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
