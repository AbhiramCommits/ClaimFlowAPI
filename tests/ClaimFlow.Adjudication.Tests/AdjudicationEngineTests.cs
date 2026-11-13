using ClaimFlow.Adjudication.Services;
using ClaimFlow.Shared;
using FluentAssertions;
using Xunit;

namespace ClaimFlow.Adjudication.Tests;

public sealed class AdjudicationEngineTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);
    private readonly AdjudicationEngine _engine = new();

    [Fact]
    public void Clean_claim_on_a_100_percent_plan_is_paid_in_full()
    {
        var request = Request("PAYER-BCBS", ("99213", "J45.909", 10_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.Outcome.Should().Be(AdjudicationOutcome.Paid);
        result.DenialReasonCode.Should().BeNull();
        result.AllowedCents.Should().Be(6_800);
        result.PaidCents.Should().Be(6_800);
        result.PatientResponsibilityCents.Should().Be(0);
        result.Lines.Single().AllowedCents.Should().Be(6_800);
        result.AppliedRules.Should().Contain("FEE_SCHEDULE");
    }

    [Fact]
    public void Contractual_fee_schedule_reduces_allowed_and_coinsurance_splits_payment()
    {
        var request = Request("PAYER-AETNA", ("99213", "J45.909", 10_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.Outcome.Should().Be(AdjudicationOutcome.PartiallyPaid);
        result.AllowedCents.Should().Be(7_200);
        result.PaidCents.Should().Be(6_480);
        result.PatientResponsibilityCents.Should().Be(720);
        result.DenialReasonCode.Should().BeNull();
    }

    [Fact]
    public void Termed_coverage_denies_with_CO27_and_zero_amounts()
    {
        var request = Request("PAYER-AETNA", ("99213", "J45.909", 10_000));
        var eligibility = new EligibilityResult(false, EligibilityStatus.Termed,
            DenialReasonCode.CO27, 0, new DateOnly(2025, 1, 1), "terminated");

        var result = _engine.Adjudicate(request, eligibility, Today);

        result.Outcome.Should().Be(AdjudicationOutcome.Denied);
        result.DenialReasonCode.Should().Be(DenialReasonCode.CO27);
        result.AllowedCents.Should().Be(0);
        result.PaidCents.Should().Be(0);
        result.PatientResponsibilityCents.Should().Be(0);
        result.Lines.Should().OnlyContain(l => l.AllowedCents == 0 && l.PaidCents == 0);
        result.AppliedRules.Should().Contain("COVERAGE_TERMINATED_CO27");
    }

    [Fact]
    public void Timely_filing_expiry_denies_with_CO29()
    {
        var request = Request("PAYER-AETNA", Today.AddDays(-181), ("99213", "J45.909", 10_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.Outcome.Should().Be(AdjudicationOutcome.Denied);
        result.DenialReasonCode.Should().Be(DenialReasonCode.CO29);
        result.AppliedRules.Should().Contain("TIMELY_FILING_CO29");
    }

    [Fact]
    public void Medical_necessity_mismatch_denies_with_CO50()
    {
        var request = Request("PAYER-BCBS", ("27447", "J45.909", 200_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.Outcome.Should().Be(AdjudicationOutcome.Denied);
        result.DenialReasonCode.Should().Be(DenialReasonCode.CO50);
        result.AppliedRules.Should().Contain("MEDICAL_NECESSITY_CO50");
    }

    [Fact]
    public void Medical_necessity_supported_diagnosis_is_not_denied()
    {
        var request = Request("PAYER-BCBS", ("27447", "M17.11", 200_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.DenialReasonCode.Should().NotBe(DenialReasonCode.CO50);
        result.Outcome.Should().NotBe(AdjudicationOutcome.Denied);
    }

    [Fact]
    public void Physical_therapy_with_supporting_diagnosis_passes_medical_necessity()
    {
        var request = Request("PAYER-BCBS", ("97110", "M54.5", 8_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.DenialReasonCode.Should().NotBe(DenialReasonCode.CO50);
    }

    [Fact]
    public void Bundled_evaluation_and_management_line_is_zeroed_with_CO97()
    {
        var request = Request("PAYER-BCBS",
            ("99213", "J45.909", 8_000),
            ("99215", "J45.909", 20_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.DenialReasonCode.Should().Be(DenialReasonCode.CO97);
        result.Outcome.Should().Be(AdjudicationOutcome.PartiallyPaid);
        result.Lines.Single(l => l.CptCode == "99213").AllowedCents.Should().Be(0);
        result.Lines.Single(l => l.CptCode == "99213").AdjustmentReason.Should().StartWith("CO97");
        result.Lines.Single(l => l.CptCode == "99215").AllowedCents.Should().BeGreaterThan(0);
        result.PaidCents.Should().BeGreaterThan(0);
        result.AppliedRules.Should().Contain("BUNDLED_CO97");
    }

    [Fact]
    public void Non_covered_procedure_becomes_patient_responsibility_with_PR204()
    {
        var request = Request("PAYER-CIGNA", ("99396", "Z00.00", 12_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.DenialReasonCode.Should().Be(DenialReasonCode.PR204);
        result.Outcome.Should().Be(AdjudicationOutcome.PartiallyPaid);
        result.PaidCents.Should().Be(0);
        result.PatientResponsibilityCents.Should().Be(12_000);
        result.Lines.Single().AdjustmentReason.Should().StartWith("PR204");
        result.AppliedRules.Should().Contain("NON_COVERED_PR204");
    }

    [Fact]
    public void Deductible_is_applied_before_coinsurance_and_coded_PR1()
    {
        var request = Request("PAYER-BCBS", ("99213", "J45.909", 10_000));
        var eligibility = new EligibilityResult(true, EligibilityStatus.DeductibleRemaining, null,
            5_000, null, "deductible");

        var result = _engine.Adjudicate(request, eligibility, Today);

        result.DenialReasonCode.Should().Be(DenialReasonCode.PR1);
        result.Outcome.Should().Be(AdjudicationOutcome.PartiallyPaid);
        result.AllowedCents.Should().Be(6_800);
        result.PatientResponsibilityCents.Should().Be(5_000);
        result.PaidCents.Should().Be(1_800);
        result.AppliedRules.Should().Contain("DEDUCTIBLE_PR1");
    }

    [Fact]
    public void Deductible_larger_than_allowed_leaves_zero_payer_payment()
    {
        var request = Request("PAYER-BCBS", ("99213", "J45.909", 1_000));
        var eligibility = new EligibilityResult(true, EligibilityStatus.DeductibleRemaining, null,
            5_000, null, "deductible");

        var result = _engine.Adjudicate(request, eligibility, Today);

        result.PaidCents.Should().Be(0);
        result.PatientResponsibilityCents.Should().Be(680);
        result.DenialReasonCode.Should().Be(DenialReasonCode.PR1);
        result.Outcome.Should().Be(AdjudicationOutcome.PartiallyPaid);
    }

    [Fact]
    public void Unknown_payer_is_denied_with_CO109()
    {
        var request = Request("PAYER-NOT-REAL", ("99213", "J45.909", 10_000));

        var result = _engine.Adjudicate(request, Eligible(), Today);

        result.Outcome.Should().Be(AdjudicationOutcome.Denied);
        result.DenialReasonCode.Should().Be(DenialReasonCode.CO109);
    }

    [Fact]
    public void Hard_denial_lines_all_carry_zero_payment()
    {
        var request = Request("PAYER-BCBS",
            ("99213", "J45.909", 10_000),
            ("93000", "J45.909", 5_000));

        var result = _engine.Adjudicate(request, new EligibilityResult(false, EligibilityStatus.Termed,
            DenialReasonCode.CO27, 0, null, "terminated"), Today);

        result.Lines.Should().HaveCount(2);
        result.Lines.Should().OnlyContain(l =>
            l.AllowedCents == 0 && l.PaidCents == 0 && l.PatientResponsibilityCents == 0);
    }

    private static EligibilityResult Eligible(int deductible = 0) => new(
        true,
        deductible > 0 ? EligibilityStatus.DeductibleRemaining : EligibilityStatus.Active,
        null,
        deductible,
        null,
        "eligible");

    private static AdjudicationRequest Request(string payerId, params (string Cpt, string Dx, int Charge)[] lines) =>
        Request(payerId, Today.AddDays(-10), lines);

    private static AdjudicationRequest Request(
        string payerId,
        DateOnly serviceDate,
        params (string Cpt, string Dx, int Charge)[] lines) => new(
        "CLM-TEST-0001",
        payerId,
        "MBR-000001",
        serviceDate,
        lines.Select((line, index) => new AdjudicationLineRequest(
            index + 1, line.Cpt, line.Dx, line.Charge)).ToArray(),
        lines.Select(line => line.Dx).Distinct().ToArray());
}
