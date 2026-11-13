using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace ClaimFlow.Security.Logging;

public static class ClaimFlowSerilog
{
    public static LoggerConfiguration ConfigureClaimFlow(
        this LoggerConfiguration configuration,
        IConfiguration appConfiguration,
        IServiceProvider services,
        string serviceName)
    {
        configuration
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.With<PhiMaskingEnricher>()
            .Enrich.WithProperty("service", serviceName)
            .Destructure.With<PhiDestructuringPolicy>();

        if (appConfiguration.GetValue("Serilog:Console", true))
        {
            configuration.WriteTo.Console(new JsonFormatter(renderMessage: true));
        }

        if (appConfiguration.GetValue("Serilog:InMemory", false))
        {
            configuration.WriteTo.Sink(services.GetRequiredService<InMemorySink>());
        }

        return configuration;
    }

    public static IServiceCollection AddClaimFlowLogging(this IServiceCollection services)
    {
        services.AddSingleton<InMemorySink>();
        return services;
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "ClaimFlow.CorrelationId";
    public const string LogPropertyName = "CorrelationId";

    public static IApplicationBuilder UseClaimFlowCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(correlationId))
            {
                correlationId = context.TraceIdentifier;
            }

            correlationId = correlationId.Trim();
            if (correlationId.Length > 64)
            {
                correlationId = correlationId[..64];
            }

            context.Items[ItemKey] = correlationId;
            context.Response.Headers[HeaderName] = correlationId;

            using (Serilog.Context.LogContext.PushProperty(LogPropertyName, correlationId))
            {
                await next();
            }
        });

    public static string GetCorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string correlationId
            ? correlationId
            : context.TraceIdentifier;

    public static IApplicationBuilder UseClaimFlowRequestLogging(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("CorrelationId", httpContext.GetCorrelationId());
                diagnosticContext.Set("RequestMethod", httpContext.Request.Method);
                diagnosticContext.Set("RequestPath", httpContext.Request.Path.Value ?? string.Empty);
                diagnosticContext.Set("StatusCode", httpContext.Response.StatusCode);
                diagnosticContext.Set("TraceId", Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier);
            };
        });
}
