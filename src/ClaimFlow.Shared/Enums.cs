namespace ClaimFlow.Shared;

/// <summary>Lifecycle of a claim as it moves through intake, adjudication and payment.</summary>
public enum ClaimStatus
{
    Received = 0,
    Validated = 1,
    Rejected = 2,
    Adjudicated = 3,
    Paid = 4,
    Denied = 5,
}

/// <summary>Financial result of adjudication. Paid + PartiallyPaid + Denied partitions the outcome mix.</summary>
public enum AdjudicationOutcome
{
    Paid = 0,
    PartiallyPaid = 1,
    Denied = 2,
}

public enum CoverageStatus
{
    Active = 0,
    Termed = 1,
}

/// <summary>X12 CARC/RARC-style denial and patient-responsibility reason codes.</summary>
public enum DenialReasonCode
{
    /// <summary>CO-16: Claim/service lacks information or has submission/billing error.</summary>
    CO16 = 16,

    /// <summary>CO-18: Duplicate claim/service.</summary>
    CO18 = 18,

    /// <summary>CO-27: Expenses incurred after coverage terminated.</summary>
    CO27 = 27,

    /// <summary>CO-29: The time limit for filing has expired.</summary>
    CO29 = 29,

    /// <summary>CO-50: Non-covered because not deemed a medical necessity.</summary>
    CO50 = 50,

    /// <summary>CO-97: The benefit for this service is included in another service (bundled).</summary>
    CO97 = 97,

    /// <summary>PR-1: Deductible amount.</summary>
    PR1 = 1,

    /// <summary>PR-204: Service/equipment/drug is not covered under the patient's current benefit plan.</summary>
    PR204 = 204,

    /// <summary>CO-11: Diagnosis inconsistent with the procedure.</summary>
    CO11 = 11,

    /// <summary>CO-109: Claim/service not covered by this payer/contractor.</summary>
    CO109 = 109,

    /// <summary>CO-151: Payment adjusted because the payer deems the information submitted does not support this many services.</summary>
    CO151 = 151,
}

public static class DenialReasons
{
    private static readonly IReadOnlyDictionary<DenialReasonCode, string> Descriptions =
        new Dictionary<DenialReasonCode, string>
        {
            [DenialReasonCode.CO11] = "Diagnosis inconsistent with the procedure",
            [DenialReasonCode.CO16] = "Claim/service lacks information or has submission/billing error",
            [DenialReasonCode.CO18] = "Duplicate claim/service",
            [DenialReasonCode.CO27] = "Expenses incurred after coverage terminated",
            [DenialReasonCode.CO29] = "The time limit for filing has expired",
            [DenialReasonCode.CO50] = "Non-covered because not deemed a medical necessity",
            [DenialReasonCode.CO97] = "Benefit included in another service (bundled)",
            [DenialReasonCode.CO109] = "Claim/service not covered by this payer/contractor",
            [DenialReasonCode.CO151] = "Information does not support this many services",
            [DenialReasonCode.PR1] = "Deductible amount",
            [DenialReasonCode.PR204] = "Service not covered under the patient's current benefit plan",
        };

    public static string Describe(DenialReasonCode code) =>
        Descriptions.TryGetValue(code, out var description) ? description : code.ToString();

    public static string Code(DenialReasonCode code) => code switch
    {
        DenialReasonCode.PR1 => "PR1",
        DenialReasonCode.PR204 => "PR204",
        _ => $"CO{(int)code}",
    };

    public static IReadOnlyCollection<DenialReasonCode> All { get; } = Descriptions.Keys.ToArray();
}
