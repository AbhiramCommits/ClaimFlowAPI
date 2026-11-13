using ClaimFlow.Shared;

namespace ClaimFlow.Adjudication.Services;

public sealed record EligibilityRequest(string MemberId, string PayerId, DateOnly ServiceDate);

public sealed record EligibilityResult(
    bool Eligible,
    EligibilityStatus Status,
    DenialReasonCode? DenialReasonCode,
    int DeductibleRemainingCents,
    DateOnly? TerminationDate,
    string Message);

public enum EligibilityStatus
{
    Active,
    Termed,
    DeductibleRemaining,
}

public sealed record SyntheticCoverage(
    CoverageStatus Status,
    DateOnly EffectiveDate,
    DateOnly? TerminationDate,
    int DeductibleRemainingCents);

/// <summary>
/// Deterministic in-process coverage store. Member ids map to active, termed and
/// deductible-remaining states via a stable SHA-256 bucket, so runs are reproducible.
/// Tests can pin a member with <see cref="Set"/>.
/// </summary>
public sealed class SyntheticCoverageStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SyntheticCoverage> _overrides =
        new(StringComparer.OrdinalIgnoreCase);

    public void Set(string memberId, SyntheticCoverage coverage) =>
        _overrides[memberId.Trim().ToUpperInvariant()] = coverage;

    public SyntheticCoverage Get(string memberId)
    {
        var normalized = (memberId ?? string.Empty).Trim().ToUpperInvariant();
        if (_overrides.TryGetValue(normalized, out var pinned))
        {
            return pinned;
        }

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        var bucket = hash[0] % 100;
        if (bucket < 6)
        {
            return new SyntheticCoverage(
                CoverageStatus.Termed,
                new DateOnly(2024, 1, 1),
                new DateOnly(2025, 1, 1),
                0);
        }

        if (bucket < 26)
        {
            var deductible = (((hash[1] << 8) | hash[2]) % 451 + 50) * 100;
            return new SyntheticCoverage(CoverageStatus.Active, new DateOnly(2024, 1, 1), null, deductible);
        }

        return new SyntheticCoverage(CoverageStatus.Active, new DateOnly(2024, 1, 1), null, 0);
    }

    public IEnumerable<string> FindMembersWithDeductible(int count) =>
        FindMembers(coverage => coverage.Status == CoverageStatus.Active && coverage.DeductibleRemainingCents > 0, count);

    public IEnumerable<string> FindActiveMembersWithoutDeductible(int count) =>
        FindMembers(coverage => coverage.Status == CoverageStatus.Active && coverage.DeductibleRemainingCents == 0, count);

    private IEnumerable<string> FindMembers(Func<SyntheticCoverage, bool> predicate, int count)
    {
        var found = 0;
        for (var i = 0; found < count && i < 100_000; i++)
        {
            var memberId = $"MBR-{i:D6}";
            if (!predicate(Get(memberId)))
            {
                continue;
            }

            found++;
            yield return memberId;
        }
    }
}

public interface IEligibilityService
{
    EligibilityResult Check(EligibilityRequest request);
}

public sealed class EligibilityService : IEligibilityService
{
    private readonly SyntheticCoverageStore _store;

    public EligibilityService(SyntheticCoverageStore store) => _store = store;

    public EligibilityResult Check(EligibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var coverage = _store.Get(request.MemberId);

        if (coverage.Status == CoverageStatus.Termed)
        {
            return new EligibilityResult(false, EligibilityStatus.Termed, DenialReasonCode.CO27,
                coverage.DeductibleRemainingCents, coverage.TerminationDate,
                "Coverage is terminated for this member.");
        }

        if (coverage.TerminationDate is { } termination && request.ServiceDate > termination)
        {
            return new EligibilityResult(false, EligibilityStatus.Termed, DenialReasonCode.CO27,
                coverage.DeductibleRemainingCents, termination,
                $"Coverage terminated on {termination:yyyy-MM-dd}, before the service date.");
        }

        if (request.ServiceDate < coverage.EffectiveDate)
        {
            return new EligibilityResult(false, EligibilityStatus.Termed, DenialReasonCode.CO27,
                coverage.DeductibleRemainingCents, null,
                $"Coverage was not effective until {coverage.EffectiveDate:yyyy-MM-dd}.");
        }

        var status = coverage.DeductibleRemainingCents > 0
            ? EligibilityStatus.DeductibleRemaining
            : EligibilityStatus.Active;

        return new EligibilityResult(true, status, null, coverage.DeductibleRemainingCents,
            coverage.TerminationDate, status == EligibilityStatus.DeductibleRemaining
                ? $"Coverage active with {coverage.DeductibleRemainingCents} cents of deductible remaining."
                : "Coverage active with no deductible remaining.");
    }
}
