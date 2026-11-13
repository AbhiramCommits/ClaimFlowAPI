using ClaimFlow.Shared;

namespace ClaimFlow.Intake.Infrastructure;

public static class HttpContextExtensions
{
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>Returns the propagated correlation id, generating one from the trace identifier when absent.</summary>
    public static string GetCorrelationId(this HttpContext context)
    {
        if (context.Items.TryGetValue(CorrelationHeader, out var existing) && existing is string value && value.Length > 0)
        {
            return value;
        }

        if (context.Request.Headers.TryGetValue(CorrelationHeader, out var header) &&
            !string.IsNullOrWhiteSpace(header))
        {
            var fromHeader = header.ToString();
            context.Items[CorrelationHeader] = fromHeader;
            return fromHeader;
        }

        var generated = context.TraceIdentifier;
        context.Items[CorrelationHeader] = generated;
        return generated;
    }
}
