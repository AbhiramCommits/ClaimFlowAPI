using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;
using ClaimFlow.Shared.Normalization;
using FluentAssertions;
using Xunit;

namespace ClaimFlow.Shared.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public void Normalizer_sums_line_charges_into_integer_cents()
    {
        var bundle = TestClaims.Valid();
        var withTwoLines = TestClaims.WithLines(bundle,
            new ClaimLineItem { LineNumber = 1, CptCode = "99213", Icd10Code = "J45.909", PlaceOfService = "11", ChargeCents = 9_999, Units = 1 },
            new ClaimLineItem { LineNumber = 2, CptCode = "93000", Icd10Code = "J45.909", PlaceOfService = "11", ChargeCents = 5_001, Units = 1 });

        var result = ClaimNormalizer.Normalize(withTwoLines);

        result.Claim.TotalChargeCents.Should().Be(15_000);
        result.Claim.Lines.Should().HaveCount(2);
        result.Claim.Lines.Sum(l => l.ChargeCents).Should().Be(result.Claim.TotalChargeCents);
    }

    [Fact]
    public void Normalizer_trims_and_upcases_codes()
    {
        var bundle = TestClaims.Valid();
        var messy = TestClaims.WithClaim(bundle, c => new ClaimResource
        {
            ClaimId = $"  {c.ClaimId}  ",
            PayerId = "  payer-aetna ",
            MemberId = " mbr-000123 ",
            ProviderNpi = $" {c.ProviderNpi} ",
            PlaceOfService = " 11 ",
            ServiceDate = c.ServiceDate,
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = new[] { " j45.909 " },
            Lines = new[]
            {
                new ClaimLineItem { LineNumber = 1, CptCode = " 99213 ", Icd10Code = " j45.909 ", PlaceOfService = " 11 ", ChargeCents = 15_000, Units = 1 },
            },
        });

        var result = ClaimNormalizer.Normalize(messy);

        result.Claim.ClaimId.Should().Be("CLM-TEST-0001");
        result.Claim.PayerId.Should().Be("PAYER-AETNA");
        result.Claim.MemberId.Should().Be("MBR-000123");
        result.Claim.ProviderNpi.Should().Be(TestClaims.ValidNpi);
        result.Claim.PlaceOfService.Should().Be("11");
        result.Claim.Lines[0].CptCode.Should().Be("99213");
        result.Claim.Lines[0].Icd10Code.Should().Be("J45.909");
        result.Claim.Lines[0].PlaceOfService.Should().Be("11");
        result.Claim.DiagnosisCodes.Should().Equal("J45.909");
        result.Patient.Address.State.Should().Be("TX");
    }

    [Fact]
    public void Normalizer_never_persists_raw_ssn()
    {
        var result = ClaimNormalizer.Normalize(TestClaims.Valid());

        result.Patient.SsnLast4.Should().Be("6789");
        result.Patient.SsnHash.Should().HaveLength(64);
        result.Patient.SsnHash.Should().NotContain("123-45-6789");
        result.Patient.SsnHash.Should().NotContain("123456789");
    }

    [Fact]
    public void Normalizer_propagates_payer_filing_limit()
    {
        var result = ClaimNormalizer.Normalize(TestClaims.Valid());

        result.Payer.PayerId.Should().Be("PAYER-AETNA");
        result.Payer.FilingLimitDays.Should().Be(180);
        result.Payer.Name.Should().Be("Aetna");
    }

    [Fact]
    public void Normalizer_defaults_units_and_orders_lines()
    {
        var bundle = TestClaims.Valid();
        var unordered = TestClaims.WithLines(bundle,
            new ClaimLineItem { LineNumber = 2, CptCode = "93000", Icd10Code = "J45.909", PlaceOfService = "11", ChargeCents = 5_000, Units = 0 },
            new ClaimLineItem { LineNumber = 1, CptCode = "99213", Icd10Code = "J45.909", PlaceOfService = "11", ChargeCents = 10_000, Units = 2 });

        var result = ClaimNormalizer.Normalize(unordered);

        result.Claim.Lines.Select(l => l.LineNumber).Should().Equal(1, 2);
        result.Claim.Lines[1].Units.Should().Be(1);
        result.Claim.Lines[0].Units.Should().Be(2);
    }

    [Fact]
    public void Normalizer_carries_coverage_deductible()
    {
        var result = ClaimNormalizer.Normalize(TestClaims.Valid());

        result.Coverage.DeductibleRemainingCents.Should().Be(25_000);
        result.Coverage.Status.Should().Be(CoverageStatus.Active);
        result.Coverage.CoverageId.Should().Be("COV-TEST-0001");
    }
}
