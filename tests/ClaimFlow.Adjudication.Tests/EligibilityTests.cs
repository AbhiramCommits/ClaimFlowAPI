using ClaimFlow.Adjudication.Services;
using ClaimFlow.Shared;
using FluentAssertions;
using Xunit;

namespace ClaimFlow.Adjudication.Tests;

public sealed class EligibilityTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);
    private readonly SyntheticCoverageStore _store = new();
    private readonly EligibilityService _service;

    public EligibilityTests() => _service = new EligibilityService(_store);

    [Fact]
    public void Active_member_with_no_deductible_is_eligible()
    {
        _store.Set("MBR-ACTIVE", new SyntheticCoverage(CoverageStatus.Active,
            new DateOnly(2024, 1, 1), null, 0));

        var result = _service.Check(new EligibilityRequest("MBR-ACTIVE", "PAYER-AETNA", Today));

        result.Eligible.Should().BeTrue();
        result.Status.Should().Be(EligibilityStatus.Active);
        result.DeductibleRemainingCents.Should().Be(0);
        result.DenialReasonCode.Should().BeNull();
    }

    [Fact]
    public void Active_member_with_deductible_reports_deductible_remaining()
    {
        _store.Set("MBR-DEDUCTIBLE", new SyntheticCoverage(CoverageStatus.Active,
            new DateOnly(2024, 1, 1), null, 15_000));

        var result = _service.Check(new EligibilityRequest("MBR-DEDUCTIBLE", "PAYER-AETNA", Today));

        result.Eligible.Should().BeTrue();
        result.Status.Should().Be(EligibilityStatus.DeductibleRemaining);
        result.DeductibleRemainingCents.Should().Be(15_000);
    }

    [Fact]
    public void Termed_coverage_is_not_eligible_and_codes_CO27()
    {
        _store.Set("MBR-TERMED", new SyntheticCoverage(CoverageStatus.Termed,
            new DateOnly(2024, 1, 1), new DateOnly(2025, 1, 1), 0));

        var result = _service.Check(new EligibilityRequest("MBR-TERMED", "PAYER-AETNA", Today));

        result.Eligible.Should().BeFalse();
        result.Status.Should().Be(EligibilityStatus.Termed);
        result.DenialReasonCode.Should().Be(DenialReasonCode.CO27);
        result.Message.Should().Contain("terminated");
    }

    [Fact]
    public void Service_after_termination_date_is_not_eligible()
    {
        _store.Set("MBR-ENDED", new SyntheticCoverage(CoverageStatus.Active,
            new DateOnly(2024, 1, 1), new DateOnly(2026, 1, 1), 0));

        var result = _service.Check(new EligibilityRequest("MBR-ENDED", "PAYER-AETNA", Today));

        result.Eligible.Should().BeFalse();
        result.DenialReasonCode.Should().Be(DenialReasonCode.CO27);
    }

    [Fact]
    public void Service_before_effective_date_is_not_eligible()
    {
        _store.Set("MBR-NEW", new SyntheticCoverage(CoverageStatus.Active,
            new DateOnly(2026, 1, 1), null, 0));

        var result = _service.Check(new EligibilityRequest("MBR-NEW", "PAYER-AETNA", new DateOnly(2025, 6, 1)));

        result.Eligible.Should().BeFalse();
        result.DenialReasonCode.Should().Be(DenialReasonCode.CO27);
    }

    [Fact]
    public void Synthetic_store_is_deterministic_and_covers_all_states()
    {
        var first = _store.Get("MBR-000042");
        var second = new SyntheticCoverageStore().Get("MBR-000042");

        first.Should().Be(second);

        var members = Enumerable.Range(0, 500).Select(i => $"MBR-{i:D6}").ToArray();
        var states = members.Select(m => _store.Get(m).Status).Distinct().ToArray();
        states.Should().Contain(CoverageStatus.Termed);

        var deductibleMembers = _store.FindMembersWithDeductible(5).ToArray();
        deductibleMembers.Should().HaveCount(5);
        deductibleMembers.Should().OnlyContain(m => _store.Get(m).DeductibleRemainingCents > 0);
    }
}
