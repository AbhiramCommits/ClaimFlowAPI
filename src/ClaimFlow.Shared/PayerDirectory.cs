using ClaimFlow.Shared.Domain;

namespace ClaimFlow.Shared;

/// <summary>Known payer directory, including each payer's timely-filing limit.</summary>
public static class PayerDirectory
{
    private static readonly PayerProfile[] Profiles =
    {
        new("PAYER-AETNA", "Aetna", 180, 72, 90),
        new("PAYER-BCBS", "Blue Cross Blue Shield", 365, 68, 100),
        new("PAYER-CIGNA", "Cigna", 180, 70, 80),
        new("PAYER-HUMANA", "Humana", 365, 74, 90),
        new("PAYER-MEDICARE", "Medicare", 365, 80, 80),
        new("PAYER-UHC", "UnitedHealthcare", 270, 71, 100),
    };

    private static readonly Dictionary<string, PayerProfile> ById =
        Profiles.ToDictionary(p => p.PayerId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<PayerProfile> All => Profiles;

    public static bool TryGet(string? payerId, out PayerProfile profile)
    {
        if (string.IsNullOrWhiteSpace(payerId))
        {
            profile = Profiles[0];
            return false;
        }

        return ById.TryGetValue(payerId.Trim(), out profile!);
    }

    public static PayerProfile GetRequired(string payerId) =>
        TryGet(payerId, out var profile)
            ? profile
            : throw new ArgumentException($"Unknown payer '{payerId}'.", nameof(payerId));
}
