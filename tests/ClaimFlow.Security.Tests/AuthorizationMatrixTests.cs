using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClaimFlow.Security.Auth;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClaimFlow.Security.Tests;

public sealed class AuthorizationMatrixTests : IClassFixture<SecurityApiFixture>
{
    private static readonly string[] Actors =
    {
        "anonymous",
        ClaimFlowRoles.ClaimsWriter,
        ClaimFlowRoles.Adjudicator,
        ClaimFlowRoles.ReportingReader,
        ClaimFlowRoles.Admin,
    };

    private static readonly string[] Endpoints =
    {
        "intake:POST /api/claims",
        "intake:POST /api/claims/batch",
        "intake:GET /api/claims/{id}",
        "intake:GET /api/claims",
        "intake:PUT /api/claims/{id}/status",
        "intake:GET /api/v1/reports/denials",
        "intake:GET /api/v1/reports/days-in-ar",
        "intake:GET /api/v1/reports/rejects",
        "intake:GET /api/v1/reports/throughput",
        "intake:GET /health/live",
        "intake:GET /health/ready",
        "adjudication:POST /api/v1/eligibility/check",
        "adjudication:POST /api/v1/adjudication/dry-run",
        "adjudication:GET /health/live",
        "adjudication:GET /health/ready",
    };

    private readonly SecurityApiFixture _fixture;

    public AuthorizationMatrixTests(SecurityApiFixture fixture) => _fixture = fixture;

    public static IEnumerable<object[]> Matrix()
    {
        foreach (var endpoint in Endpoints)
        {
            foreach (var actor in Actors)
            {
                yield return new object[] { endpoint, actor, (int)Expected(endpoint, actor) };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Every_endpoint_enforces_the_expected_authorization(
        string endpoint,
        string actor,
        int expectedStatusCode)
    {
        var service = endpoint.StartsWith("adjudication:", StringComparison.Ordinal) ? "adjudication" : "intake";
        var client = _fixture.CreateClient(service, actor == "anonymous" ? null : actor);

        using var response = await SendAsync(client, endpoint);

        response.StatusCode.Should().Be(
            (HttpStatusCode)expectedStatusCode,
            $"{endpoint} as {actor}");
    }

    [Fact]
    public void Every_non_health_endpoint_declares_authorization_metadata()
    {
        var intakeRoutes = _fixture.Intake.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var adjudicationRoutes = _fixture.Adjudication.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>();

        var routes = intakeRoutes.Endpoints.Concat(adjudicationRoutes.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => !(e.RoutePattern.RawText ?? string.Empty).TrimStart('/')
                .StartsWith("health", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        routes.Should().NotBeEmpty();
        foreach (var route in routes)
        {
            route.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
                .Should().NotBeNull($"route '{route.RoutePattern.RawText}' must declare an authorization policy");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, string endpoint)
    {
        var key = endpoint[(endpoint.IndexOf(':') + 1)..];
        var method = key[..key.IndexOf(' ')];
        var path = key[(key.IndexOf(' ') + 1)..].Replace("{id}", _fixture.ClaimId, StringComparison.Ordinal);

        switch (method)
        {
            case "GET":
                return await client.GetAsync(path);
            case "POST" when path == "/api/claims":
                return await client.PostAsJsonAsync(path,
                    SecurityTestData.Bundle($"mx{Guid.NewGuid():N}"[..10]), SecurityJson.Options);
            case "POST" when path == "/api/claims/batch":
                {
                    var ndjson = System.Text.Json.JsonSerializer.Serialize(
                        SecurityTestData.Bundle($"mb{Guid.NewGuid():N}"[..10]), SecurityJson.Options);
                    using var content = new StringContent(ndjson, Encoding.UTF8, "application/x-ndjson");
                    return await client.PostAsync(path, content);
                }
            case "POST" when path == "/api/v1/eligibility/check":
                return await client.PostAsJsonAsync(path, new
                {
                    memberId = "MBR-000001",
                    payerId = "PAYER-BCBS",
                    serviceDate = "2026-06-01",
                });
            case "POST" when path == "/api/v1/adjudication/dry-run":
                return await client.PostAsJsonAsync(path, new
                {
                    payerId = "PAYER-BCBS",
                    memberId = "MBR-000001",
                    serviceDate = "2026-06-01",
                    lines = new[] { new { cptCode = "99213", icd10Code = "J45.909", chargeCents = 10_000 } },
                    diagnosisCodes = new[] { "J45.909" },
                });
            case "PUT":
                return await client.PutAsJsonAsync(path, new
                {
                    status = "Adjudicated",
                    allowedCents = 0,
                    paidCents = 0,
                    patientResponsibilityCents = 0,
                    reason = "authorization matrix",
                    actor = "tests",
                }, SecurityJson.Options);
            default:
                throw new InvalidOperationException($"Unsupported matrix request: {endpoint}");
        }
    }

    private static HttpStatusCode Expected(string endpoint, string actor)
    {
        var anonymous = actor == "anonymous";

        if (endpoint.Contains("/health/", StringComparison.Ordinal))
        {
            return HttpStatusCode.OK;
        }

        var writeClaims = endpoint.EndsWith("POST /api/claims", StringComparison.Ordinal) ||
                          endpoint.EndsWith("POST /api/claims/batch", StringComparison.Ordinal);
        if (writeClaims)
        {
            if (anonymous)
            {
                return HttpStatusCode.Unauthorized;
            }

            return actor is ClaimFlowRoles.ClaimsWriter or ClaimFlowRoles.Admin
                ? endpoint.Contains("batch", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.Created
                : HttpStatusCode.Forbidden;
        }

        if (endpoint.EndsWith("PUT /api/claims/{id}/status", StringComparison.Ordinal))
        {
            if (anonymous)
            {
                return HttpStatusCode.Unauthorized;
            }

            return actor is ClaimFlowRoles.Adjudicator or ClaimFlowRoles.Admin
                ? HttpStatusCode.OK
                : HttpStatusCode.Forbidden;
        }

        if (endpoint.Contains("/api/v1/eligibility/check", StringComparison.Ordinal) ||
            endpoint.Contains("/api/v1/adjudication/dry-run", StringComparison.Ordinal))
        {
            if (anonymous)
            {
                return HttpStatusCode.Unauthorized;
            }

            return actor is ClaimFlowRoles.Adjudicator or ClaimFlowRoles.Admin
                ? HttpStatusCode.OK
                : HttpStatusCode.Forbidden;
        }

        if (endpoint.Contains("/api/v1/reports/", StringComparison.Ordinal))
        {
            if (anonymous)
            {
                return HttpStatusCode.Unauthorized;
            }

            return actor is ClaimFlowRoles.ReportingReader or ClaimFlowRoles.Admin
                ? HttpStatusCode.OK
                : HttpStatusCode.Forbidden;
        }

        // GET claim detail and list.
        if (anonymous)
        {
            return HttpStatusCode.Unauthorized;
        }

        return actor is ClaimFlowRoles.ClaimsWriter or ClaimFlowRoles.Adjudicator
            or ClaimFlowRoles.ReportingReader or ClaimFlowRoles.Admin
            ? HttpStatusCode.OK
            : HttpStatusCode.Forbidden;
    }
}
