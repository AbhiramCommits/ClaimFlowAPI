using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;

namespace ClaimFlow.Security.Tests;

internal static class SecurityTestData
{
    public const string LeakMemberId = "MBR-LEAK-987654";
    public const string LeakGivenName = "Zeldaunique";
    public const string LeakFamilyName = "Quarantine";
    public const string LeakSsn = "321-54-9876";
    public const string LeakDateOfBirth = "1971-12-31";
    public const string LeakAddress = "999 Secret Lane";

    public static ClaimBundle Bundle(string tag, string? claimId = null)
    {
        var memberId = $"MBR-{tag}-{Guid.NewGuid():N}"[..24];
        var id = claimId ?? $"SEC-{tag}-{Guid.NewGuid():N}"[..28];
        return new ClaimBundle
        {
            Id = $"BND-{tag}-{Guid.NewGuid():N}"[..32],
            PayerId = "PAYER-BCBS",
            MemberId = memberId,
            Patient = new PatientResource
            {
                MemberId = memberId,
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
                PayerId = "PAYER-BCBS",
                Status = CoverageStatus.Active,
                EffectiveDate = new DateOnly(2024, 1, 1),
                DeductibleRemainingCents = 0,
            },
            Claim = new ClaimResource
            {
                ClaimId = id,
                PayerId = "PAYER-BCBS",
                MemberId = memberId,
                ProviderNpi = "1234567893",
                PlaceOfService = "11",
                ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10),
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
    }

    public static ClaimBundle PhiBundle() => new()
    {
        Id = "BND-PHI-0001",
        PayerId = "PAYER-BCBS",
        MemberId = LeakMemberId,
        Patient = new PatientResource
        {
            MemberId = LeakMemberId,
            GivenName = LeakGivenName,
            FamilyName = LeakFamilyName,
            DateOfBirth = new DateOnly(1971, 12, 31),
            Ssn = LeakSsn,
            Address = new AddressResource
            {
                Line1 = LeakAddress,
                City = "Austin",
                State = "TX",
                PostalCode = "78701",
            },
        },
        Coverage = new CoverageResource
        {
            CoverageId = "COV-PHI-0001",
            PayerId = "PAYER-BCBS",
            Status = CoverageStatus.Active,
            EffectiveDate = new DateOnly(2024, 1, 1),
            DeductibleRemainingCents = 0,
        },
        Claim = new ClaimResource
        {
            ClaimId = "SEC-PHI-0001",
            PayerId = "PAYER-BCBS",
            MemberId = LeakMemberId,
            ProviderNpi = "1234567893",
            PlaceOfService = "11",
            ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10),
            TotalChargeCents = 18_500,
            DiagnosisCodes = new[] { "J45.909" },
            Lines = new[]
            {
                new ClaimLineItem
                {
                    LineNumber = 1,
                    CptCode = "99213",
                    Icd10Code = "J45.909",
                    PlaceOfService = "11",
                    ChargeCents = 18_500,
                    Units = 1,
                },
            },
        },
    };
}
