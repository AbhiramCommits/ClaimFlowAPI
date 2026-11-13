using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace ClaimFlow.Intake.Tests;

public sealed class HealthTests : IClassFixture<IntakeApiFactory>
{
    private readonly IntakeApiFactory _factory;

    public HealthTests(IntakeApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_report_healthy(string path)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
    }
}
