using System.Text.Json;
using ClaimFlow.Data;
using ClaimFlow.Data.Entities;
using ClaimFlow.Intake.Infrastructure;
using ClaimFlow.Intake.Services;
using ClaimFlow.Shared;
using ClaimFlow.Shared.Messaging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("claims", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ClaimFlow Intake API",
        Version = "v1",
        Description = "FHIR-shaped claim intake, validation, persistence and reporting.",
    });
    options.SwaggerDoc("reports", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ClaimFlow Reporting API",
        Version = "v1",
        Description = "Denial, days-in-A/R, rejection and throughput reporting.",
    });
});

builder.Services.AddDbContext<ClaimFlowDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var resolvedProvider = configuration["Database:Provider"] ?? DatabaseProviderNames.SqlServer;
    var resolvedConnection = configuration.GetConnectionString("ClaimFlow")
        ?? throw new InvalidOperationException("ConnectionStrings:ClaimFlow is required.");
    options.UseClaimFlowDatabase(resolvedProvider, resolvedConnection);
});
builder.Services.AddScoped<ClaimIngestService>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddDbContextCheck<ClaimFlowDbContext>("sql", tags: new[] { "ready" });

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/claims/swagger.json", "ClaimFlow Intake v1");
        options.SwaggerEndpoint("/swagger/reports/swagger.json", "ClaimFlow Reports v1");
    });
}

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = WriteHealthResponse,
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthResponse,
});

if (string.Equals(app.Configuration["Database:Provider"] ?? DatabaseProviderNames.SqlServer,
        DatabaseProviderNames.Sqlite, StringComparison.OrdinalIgnoreCase))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ClaimFlowDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.Run();

static Task WriteHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    var payload = new
    {
        status = report.Status.ToString(),
        durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
        checks = report.Entries.Select(e => new
        {
            name = e.Key,
            status = e.Value.Status.ToString(),
            durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 2),
        }),
    };
    return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}

/// <summary>Exposed for WebApplicationFactory integration tests.</summary>
public partial class Program
{
}
