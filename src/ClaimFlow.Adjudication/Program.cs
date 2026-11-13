using System.Text.Json;
using ClaimFlow.Adjudication.Services;
using ClaimFlow.Data;
using ClaimFlow.Messaging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Polly;

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
    options.SwaggerDoc("adjudication", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ClaimFlow Adjudication API",
        Version = "v1",
        Description = "Eligibility checks, dry-run adjudication, denial coding and claim status updates.",
    });
});

builder.Services.AddDbContext<ClaimFlowDbContext>((serviceProvider, options) =>
{
    var configuration = serviceProvider.GetRequiredService<IConfiguration>();
    var provider = configuration["Database:Provider"] ?? DatabaseProviderNames.SqlServer;
    var connectionString = configuration.GetConnectionString("ClaimFlow")
        ?? throw new InvalidOperationException("ConnectionStrings:ClaimFlow is required.");
    options.UseClaimFlowDatabase(provider, connectionString);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SyntheticCoverageStore>();
builder.Services.AddSingleton<IEligibilityService, EligibilityService>();
builder.Services.AddSingleton<AdjudicationEngine>();
builder.Services.AddClaimEventBus();
builder.Services.AddHostedService<AdjudicationConsumer>();

builder.Services
    .AddHttpClient<IntakeStatusClient>((serviceProvider, client) =>
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var baseUrl = configuration["Intake:BaseUrl"] ?? "http://localhost:5080";
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(15);
    })
    .AddTransientHttpErrorPolicy(policy => policy.WaitAndRetryAsync(
        3,
        attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt))));

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
        options.SwaggerEndpoint("/swagger/adjudication/swagger.json", "ClaimFlow Adjudication v1");
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
