using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaimFlow.Shared.Messaging;

public static class ClaimEventTypes
{
    public const string Exchange = "claimflow";
    public const string ClaimSubmitted = "claim.submitted";
    public const string ClaimAdjudicated = "claim.adjudicated";
    public const string DeadLetter = "claim.dead-letter";
}

/// <summary>Envelope carried on the bus. Data is the event-specific payload.</summary>
public sealed record ClaimEventEnvelope(
    Guid EventId,
    string EventType,
    string CorrelationId,
    DateTimeOffset OccurredAt,
    JsonElement Data);

public sealed record ClaimSubmittedPayload(
    Guid EventId,
    string ClaimId,
    string PayerId,
    string MemberId,
    string ProviderNpi,
    string PlaceOfService,
    DateOnly ServiceDate,
    int TotalChargeCents,
    IReadOnlyList<ClaimLinePayload> Lines,
    string CorrelationId,
    DateTimeOffset OccurredAt);

public sealed record ClaimLinePayload(
    int LineNumber,
    string CptCode,
    string Icd10Code,
    string PlaceOfService,
    int ChargeCents,
    int Units);

public sealed record ClaimAdjudicatedPayload(
    Guid EventId,
    string ClaimId,
    string PayerId,
    string MemberId,
    string Outcome,
    string? DenialReasonCode,
    int AllowedCents,
    int PaidCents,
    int PatientResponsibilityCents,
    string CorrelationId,
    DateTimeOffset AdjudicatedAt);

public static class ClaimEventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(JsonElement element) => element.Deserialize<T>(Options);
}
