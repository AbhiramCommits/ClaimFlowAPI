using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClaimFlow.Shared;

namespace ClaimFlow.Adjudication.Services;

public sealed record ClaimStatusUpdatePayload(
    ClaimStatus Status,
    AdjudicationOutcome Outcome,
    DenialReasonCode? DenialReasonCode,
    int AllowedCents,
    int PaidCents,
    int PatientResponsibilityCents,
    string Reason,
    Guid EventId,
    string Actor);

/// <summary>Typed client for Intake's status endpoint; Polly handles transient retries.</summary>
public sealed class IntakeStatusClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _httpClient;

    public IntakeStatusClient(HttpClient httpClient) => _httpClient = httpClient;

    public async Task UpdateStatusAsync(string claimId, ClaimStatusUpdatePayload payload, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PutAsJsonAsync(
            $"api/claims/{Uri.EscapeDataString(claimId)}/status",
            payload,
            Json,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Intake status update failed with {(int)response.StatusCode}: {body}",
                null,
                response.StatusCode);
        }
    }
}
