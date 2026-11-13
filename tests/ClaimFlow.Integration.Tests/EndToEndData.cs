using ClaimFlow.Shared;
using ClaimFlow.Shared.Dtos;

namespace ClaimFlow.Integration.Tests;

internal static class EndToEndData
{
    public static ClaimBundle Bundle(
        string tag,
        string memberId,
        string payerId = "PAYER-BCBS",
        string cptCode = "99213",
        string diagnosis = "J45.909",
        int chargeCents = 10_000)
    {
        var claimId = $"E2E-{tag}-{Guid.NewGuid():N}"[..32];
        return new ClaimBundle
        {
            Id = $"BND-{tag}-{Guid.NewGuid():N}"[..32],
            PayerId = payerId,
            MemberId = memberId,
            Patient = new PatientResource
            {
                MemberId = memberId,
                GivenName = "Morgan",
                FamilyName = "Reese",
                DateOfBirth = new DateOnly(1979, 2, 2),
                Ssn = "888-77-6666",
                Address = new AddressResource
                {
                    Line1 = "700 Congress Ave",
                    City = "Austin",
                    State = "TX",
                    PostalCode = "78701",
                },
            },
            Coverage = new CoverageResource
            {
                CoverageId = $"COV-{tag}-{Guid.NewGuid():N}"[..16],
                PayerId = payerId,
                Status = CoverageStatus.Active,
                EffectiveDate = new DateOnly(2024, 1, 1),
                DeductibleRemainingCents = 0,
            },
            Claim = new ClaimResource
            {
                ClaimId = claimId,
                PayerId = payerId,
                MemberId = memberId,
                ProviderNpi = "1234567893",
                PlaceOfService = "11",
                ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10),
                TotalChargeCents = chargeCents,
                DiagnosisCodes = new[] { diagnosis },
                Lines = new[]
                {
                    new ClaimLineItem
                    {
                        LineNumber = 1,
                        CptCode = cptCode,
                        Icd10Code = diagnosis,
                        PlaceOfService = "11",
                        ChargeCents = chargeCents,
                        Units = 1,
                    },
                },
            },
        };
    }
}
