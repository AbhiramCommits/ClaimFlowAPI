using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;

namespace ClaimFlow.Intake.Tests;

internal static class TestData
{
    public const string ValidNpi = "1234567893";

    public static ClaimBundle Bundle(string tag, string? claimId = null, string? memberId = null, DateOnly? serviceDate = null)
    {
        var id = claimId ?? $"CLM-{tag}-{Guid.NewGuid():N}"[..24];
        var member = memberId ?? $"MBR-{tag}-{Guid.NewGuid():N}"[..22];
        var date = serviceDate ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10);

        return new ClaimBundle
        {
            Id = $"BND-{tag}-{Guid.NewGuid():N}"[..24],
            PayerId = "PAYER-AETNA",
            MemberId = member,
            Patient = new PatientResource
            {
                MemberId = member,
                GivenName = "Taylor",
                FamilyName = "Morgan",
                DateOfBirth = new DateOnly(1988, 5, 4),
                Ssn = "999-11-2222",
                Address = new AddressResource
                {
                    Line1 = "42 Elm Street",
                    City = "Austin",
                    State = "TX",
                    PostalCode = "78701",
                },
            },
            Coverage = new CoverageResource
            {
                CoverageId = $"COV-{tag}-{Guid.NewGuid():N}"[..16],
                PayerId = "PAYER-AETNA",
                Status = CoverageStatus.Active,
                EffectiveDate = new DateOnly(2024, 1, 1),
                DeductibleRemainingCents = 0,
            },
            Claim = new ClaimResource
            {
                ClaimId = id,
                PayerId = "PAYER-AETNA",
                MemberId = member,
                ProviderNpi = ValidNpi,
                PlaceOfService = "11",
                ServiceDate = date,
                TotalChargeCents = 22_500,
                DiagnosisCodes = new[] { "J45.909" },
                Lines = new[]
                {
                    new ClaimLineItem
                    {
                        LineNumber = 1,
                        CptCode = "99213",
                        Icd10Code = "J45.909",
                        PlaceOfService = "11",
                        ChargeCents = 15_000,
                        Units = 1,
                    },
                    new ClaimLineItem
                    {
                        LineNumber = 2,
                        CptCode = "93000",
                        Icd10Code = "J45.909",
                        PlaceOfService = "11",
                        ChargeCents = 7_500,
                        Units = 1,
                    },
                },
            },
        };
    }

    public static ClaimBundle WithSingleLine(ClaimBundle bundle, int chargeCents = 15_000) => new()
    {
        ResourceType = bundle.ResourceType,
        Id = bundle.Id,
        PayerId = bundle.PayerId,
        MemberId = bundle.MemberId,
        Patient = bundle.Patient,
        Coverage = bundle.Coverage,
        Claim = new ClaimResource
        {
            ClaimId = bundle.Claim.ClaimId,
            PayerId = bundle.Claim.PayerId,
            MemberId = bundle.Claim.MemberId,
            ProviderNpi = bundle.Claim.ProviderNpi,
            PlaceOfService = bundle.Claim.PlaceOfService,
            ServiceDate = bundle.Claim.ServiceDate,
            TotalChargeCents = chargeCents,
            DiagnosisCodes = bundle.Claim.DiagnosisCodes,
            Lines = new[]
            {
                new ClaimLineItem
                {
                    LineNumber = 1,
                    CptCode = "99213",
                    Icd10Code = "J45.909",
                    PlaceOfService = bundle.Claim.PlaceOfService,
                    ChargeCents = chargeCents,
                    Units = 1,
                },
            },
        },
    };
}
