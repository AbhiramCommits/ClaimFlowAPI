using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;
using ClaimFlow.Shared.Validation;
using FluentAssertions;
using Xunit;

namespace ClaimFlow.Shared.Tests;

public sealed class ValidationRulesTests
{
    [Fact]
    public void Valid_bundle_passes_with_no_errors()
    {
        var result = ClaimValidator.Validate(TestClaims.Valid(), TestClaims.Today);

        result.IsValid.Should().BeTrue();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Missing_member_id_is_rejected()
    {
        var bundle = TestClaims.Valid();
        var invalid = new ClaimBundle
        {
            Id = bundle.Id,
            PayerId = bundle.PayerId,
            MemberId = string.Empty,
            Patient = new PatientResource
            {
                MemberId = string.Empty,
                GivenName = bundle.Patient.GivenName,
                FamilyName = bundle.Patient.FamilyName,
                DateOfBirth = bundle.Patient.DateOfBirth,
                Ssn = bundle.Patient.Ssn,
                Address = bundle.Patient.Address,
            },
            Coverage = bundle.Coverage,
            Claim = bundle.Claim,
        };

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.MissingMemberId)
            .Which.Field.Should().Be("memberId");
    }

    [Fact]
    public void Unknown_payer_is_rejected()
    {
        var bundle = TestClaims.Valid();
        var invalid = new ClaimBundle
        {
            Id = bundle.Id,
            PayerId = "PAYER-NOT-REAL",
            MemberId = bundle.MemberId,
            Patient = bundle.Patient,
            Coverage = bundle.Coverage,
            Claim = bundle.Claim,
        };

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.UnknownPayer);
    }

    [Fact]
    public void Future_service_date_is_rejected()
    {
        var invalid = TestClaims.WithClaim(TestClaims.Valid(), c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = TestClaims.Today.AddDays(5),
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.FutureServiceDate);
    }

    [Fact]
    public void Service_date_older_than_payer_filing_limit_is_rejected()
    {
        // Aetna's limit is 180 days.
        var invalid = TestClaims.WithClaim(TestClaims.Valid(), c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = TestClaims.Today.AddDays(-181),
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.FilingLimitExceeded);
    }

    [Fact]
    public void Service_date_inside_filing_limit_is_accepted()
    {
        var valid = TestClaims.WithClaim(TestClaims.Valid(), c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = TestClaims.Today.AddDays(-179),
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        });

        ClaimValidator.Validate(valid, TestClaims.Today).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Invalid_npi_checksum_is_rejected()
    {
        var invalid = TestClaims.WithClaim(TestClaims.Valid(), c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = "1234567890",
            PlaceOfService = c.PlaceOfService,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.InvalidNpi);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123456789")]
    [InlineData("12345678901")]
    [InlineData("3234567893")]
    [InlineData("12345A7893")]
    public void Malformed_npi_is_rejected(string npi)
    {
        NpiValidator.IsValid(npi).Should().BeFalse();
    }

    [Fact]
    public void Known_good_npi_passes_luhn()
    {
        NpiValidator.IsValid(TestClaims.ValidNpi).Should().BeTrue();
    }

    [Fact]
    public void Invalid_icd10_is_rejected()
    {
        var bundle = TestClaims.Valid();
        var invalid = TestClaims.WithLines(bundle, new ClaimLineItem
        {
            LineNumber = 1,
            CptCode = "99213",
            Icd10Code = "ZZZ999",
            PlaceOfService = "11",
            ChargeCents = 15_000,
            Units = 1,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().Contain(e => e.RuleCode == ValidationRuleCodes.InvalidIcd10);
    }

    [Fact]
    public void Invalid_cpt_is_rejected()
    {
        var bundle = TestClaims.Valid();
        var invalid = TestClaims.WithLines(bundle, new ClaimLineItem
        {
            LineNumber = 1,
            CptCode = "ABCDE",
            Icd10Code = "J45.909",
            PlaceOfService = "11",
            ChargeCents = 15_000,
            Units = 1,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.InvalidCpt);
    }

    [Fact]
    public void Zero_line_charge_is_rejected()
    {
        var bundle = TestClaims.Valid();
        var invalid = TestClaims.WithLines(bundle, new ClaimLineItem
        {
            LineNumber = 1,
            CptCode = "99213",
            Icd10Code = "J45.909",
            PlaceOfService = "11",
            ChargeCents = 0,
            Units = 1,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().Contain(e => e.RuleCode == ValidationRuleCodes.NonPositiveCharge);
    }

    [Fact]
    public void Negative_line_charge_is_rejected()
    {
        var bundle = TestClaims.Valid();
        var invalid = TestClaims.WithLines(bundle, new ClaimLineItem
        {
            LineNumber = 1,
            CptCode = "99213",
            Icd10Code = "J45.909",
            PlaceOfService = "11",
            ChargeCents = -100,
            Units = 1,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().Contain(e => e.RuleCode == ValidationRuleCodes.NonPositiveCharge);
    }

    [Fact]
    public void Line_charges_not_summing_to_total_are_rejected()
    {
        var invalid = TestClaims.WithClaim(TestClaims.Valid(), c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = c.TotalChargeCents + 1,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.LineTotalMismatch);
    }

    [Fact]
    public void Duplicate_service_line_is_rejected()
    {
        var bundle = TestClaims.Valid();
        var duplicate = new ClaimLineItem
        {
            LineNumber = 1,
            CptCode = "99213",
            Icd10Code = "J45.909",
            PlaceOfService = "11",
            ChargeCents = 15_000,
            Units = 1,
        };
        var duplicateWithNewNumber = new ClaimLineItem
        {
            LineNumber = 2,
            CptCode = "99213",
            Icd10Code = "J45.909",
            PlaceOfService = "11",
            ChargeCents = 15_000,
            Units = 1,
        };

        var invalid = TestClaims.WithLines(bundle, duplicate, duplicateWithNewNumber);

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.DuplicateLine);
    }

    [Fact]
    public void Missing_place_of_service_is_rejected()
    {
        var bundle = TestClaims.Valid();
        var invalid = TestClaims.WithClaim(bundle, c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = string.Empty,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines.Select(l => new ClaimLineItem
            {
                LineNumber = l.LineNumber,
                CptCode = l.CptCode,
                Icd10Code = l.Icd10Code,
                PlaceOfService = string.Empty,
                ChargeCents = l.ChargeCents,
                Units = l.Units,
            }).ToArray(),
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().Contain(e => e.RuleCode == ValidationRuleCodes.MissingPlaceOfService);
    }

    [Fact]
    public void Missing_service_date_is_rejected()
    {
        var invalid = TestClaims.WithClaim(TestClaims.Valid(), c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = default,
            TotalChargeCents = c.TotalChargeCents,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = c.Lines,
        });

        var result = ClaimValidator.Validate(invalid, TestClaims.Today);

        result.Errors.Should().ContainSingle(e => e.RuleCode == ValidationRuleCodes.MissingServiceDate);
    }

    [Fact]
    public void Every_validation_rule_code_has_a_description()
    {
        var codes = typeof(ValidationRuleCodes)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        codes.Should().NotBeEmpty();
        foreach (var code in codes)
        {
            ValidationRuleCodes.Descriptions.Should().ContainKey(code);
        }
    }
}
