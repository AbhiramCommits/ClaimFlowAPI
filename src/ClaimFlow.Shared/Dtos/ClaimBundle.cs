using System.Text.Json.Serialization;

namespace ClaimFlow.Shared.Dtos;

/// <summary>
/// A FHIR-inspired collection bundle carrying one claim plus its patient and coverage resources.
/// The shape is intentionally pragmatic: resources are flattened onto the bundle for streaming NDJSON.
/// </summary>
public sealed class ClaimBundle
{
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; init; } = "Bundle";

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("payerId")]
    public string PayerId { get; init; } = string.Empty;

    [JsonPropertyName("memberId")]
    public string MemberId { get; init; } = string.Empty;

    [JsonPropertyName("patient")]
    public PatientResource Patient { get; init; } = new();

    [JsonPropertyName("coverage")]
    public CoverageResource Coverage { get; init; } = new();

    [JsonPropertyName("claim")]
    public ClaimResource Claim { get; init; } = new();
}

public sealed class PatientResource
{
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; init; } = "Patient";

    [JsonPropertyName("memberId")]
    public string MemberId { get; init; } = string.Empty;

    [JsonPropertyName("givenName")]
    public string GivenName { get; init; } = string.Empty;

    [JsonPropertyName("familyName")]
    public string FamilyName { get; init; } = string.Empty;

    [JsonPropertyName("dateOfBirth")]
    public DateOnly DateOfBirth { get; init; }

    [JsonPropertyName("ssn")]
    public string Ssn { get; init; } = string.Empty;

    [JsonPropertyName("address")]
    public AddressResource Address { get; init; } = new();
}

public sealed class AddressResource
{
    [JsonPropertyName("line1")]
    public string Line1 { get; init; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; init; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; init; } = string.Empty;

    [JsonPropertyName("postalCode")]
    public string PostalCode { get; init; } = string.Empty;
}

public sealed class CoverageResource
{
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; init; } = "Coverage";

    [JsonPropertyName("coverageId")]
    public string CoverageId { get; init; } = string.Empty;

    [JsonPropertyName("payerId")]
    public string PayerId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public CoverageStatus Status { get; init; } = CoverageStatus.Active;

    [JsonPropertyName("effectiveDate")]
    public DateOnly EffectiveDate { get; init; }

    [JsonPropertyName("terminationDate")]
    public DateOnly? TerminationDate { get; init; }

    [JsonPropertyName("deductibleRemainingCents")]
    public int DeductibleRemainingCents { get; init; }
}

public sealed class ClaimResource
{
    [JsonPropertyName("resourceType")]
    public string ResourceType { get; init; } = "Claim";

    [JsonPropertyName("claimId")]
    public string ClaimId { get; init; } = string.Empty;

    [JsonPropertyName("payerId")]
    public string PayerId { get; init; } = string.Empty;

    [JsonPropertyName("memberId")]
    public string MemberId { get; init; } = string.Empty;

    [JsonPropertyName("providerNpi")]
    public string ProviderNpi { get; init; } = string.Empty;

    [JsonPropertyName("placeOfService")]
    public string PlaceOfService { get; init; } = string.Empty;

    [JsonPropertyName("serviceDate")]
    public DateOnly ServiceDate { get; init; }

    [JsonPropertyName("totalChargeCents")]
    public int TotalChargeCents { get; init; }

    [JsonPropertyName("diagnosisCodes")]
    public IReadOnlyList<string> DiagnosisCodes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("lines")]
    public IReadOnlyList<ClaimLineItem> Lines { get; init; } = Array.Empty<ClaimLineItem>();
}

public sealed class ClaimLineItem
{
    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; init; }

    [JsonPropertyName("cptCode")]
    public string CptCode { get; init; } = string.Empty;

    [JsonPropertyName("icd10Code")]
    public string Icd10Code { get; init; } = string.Empty;

    [JsonPropertyName("placeOfService")]
    public string PlaceOfService { get; init; } = string.Empty;

    [JsonPropertyName("chargeCents")]
    public int ChargeCents { get; init; }

    [JsonPropertyName("units")]
    public int Units { get; init; } = 1;
}
