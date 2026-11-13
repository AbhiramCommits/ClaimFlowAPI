using System.Net;
using System.Net.Http.Json;
using ClaimFlow.Intake.Contracts;
using FluentAssertions;
using Xunit;

namespace ClaimFlow.Intake.Tests;

public sealed class PaginationTests : IClassFixture<IntakeApiFactory>
{
    private readonly IntakeApiFactory _factory;

    public PaginationTests(IntakeApiFactory factory) => _factory = factory;

    [Fact]
    public async Task List_is_paged_and_filterable()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/claims", TestData.Bundle($"page{i}"), IntakeApiFactory.Json);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var page1 = await client.GetFromJsonAsync<PagedResult<ClaimSummaryDto>>(
            "/api/claims?status=Validated&payer=PAYER-AETNA&page=1&pageSize=2", IntakeApiFactory.Json);
        page1!.Items.Should().HaveCount(2);
        page1.TotalCount.Should().Be(5);
        page1.TotalPages.Should().Be(3);

        var page3 = await client.GetFromJsonAsync<PagedResult<ClaimSummaryDto>>(
            "/api/claims?status=Validated&payer=PAYER-AETNA&page=3&pageSize=2", IntakeApiFactory.Json);
        page3!.Items.Should().HaveCount(1);
        page3.Page.Should().Be(3);
    }

    [Fact]
    public async Task Invalid_status_filter_returns_problem_details()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/claims?status=NotAStatus");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
