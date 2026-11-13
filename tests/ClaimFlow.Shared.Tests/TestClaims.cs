using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;

namespace ClaimFlow.Shared.Tests;

internal static class TestClaims
{
    public static readonly DateOnly Today = new(2026, 6, 15);

    public const string ValidNpi = "1234567893";

    public static ClaimBundle Valid() => new()
    {
        ResourceType = "Bundle",
        Id = "BND-TEST-0001",
        PayerId = "PAYER-AETNA",
        MemberId = "MBR-000123",
        Patient = new PatientResource
        {
            MemberId = "MBR-000123",
            GivenName = "Alex",
            FamilyName = "Nguyen",
            DateOfBirth = new DateOnly(1984, 3, 12),
            Ssn = "123-45-6789",
            Address = new AddressResource
            {
                Line1 = "100 Main Street",
                City = "Austin",
                State = "tx",
                PostalCode = "78701",
            },
        },
        Coverage = new CoverageResource
        {
            CoverageId = "COV-TEST-0001",
            PayerId = "PAYER-AETNA",
            Status = CoverageStatus.Active,
            EffectiveDate = new DateOnly(2025, 1, 1),
            TerminationDate = null,
            DeductibleRemainingCents = 25_000,
        },
        Claim = new ClaimResource
        {
            ClaimId = "CLM-TEST-0001",
            PayerId = "PAYER-AETNA",
            MemberId = "MBR-000123",
            ProviderNpi = ValidNpi,
            PlaceOfService = "11",
            ServiceDate = Today.AddDays(-30),
            TotalChargeCents = 15_000,
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
            },
        },
    };

    public static ClaimBundle With(ClaimBundle source, Func<ClaimBundle, ClaimBundle> mutate) => mutate(source);

    public static ClaimBundle WithClaim(ClaimBundle source, Func<ClaimResource, ClaimResource> mutate)
    {
        var claim = source.Claim;
        var updated = mutate(claim);
        return new ClaimBundle
        {
            ResourceType = source.ResourceType,
            Id = source.Id,
            PayerId = source.PayerId,
            MemberId = source.MemberId,
            Patient = source.Patient,
            Coverage = source.Coverage,
            Claim = updated,
        };
    }

    public static ClaimBundle WithLines(ClaimBundle source, params ClaimLineItem[] lines)
    {
        var total = lines.Sum(l => l.ChargeCents);
        return WithClaim(source, c => new ClaimResource
        {
            ClaimId = c.ClaimId,
            PayerId = c.PayerId,
            MemberId = c.MemberId,
            ProviderNpi = c.ProviderNpi,
            PlaceOfService = c.PlaceOfService,
            ServiceDate = c.ServiceDate,
            TotalChargeCents = total,
            DiagnosisCodes = c.DiagnosisCodes,
            Lines = lines,
        });
    }
}
