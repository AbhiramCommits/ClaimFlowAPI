using Serilog.Core;
using Serilog.Events;

namespace ClaimFlow.Security.Logging;

/// <summary>
/// Masks scalar log properties whose names are on the PHI list. This catches properties pushed
/// through LogContext or added after destructuring, not just DTOs handled by the policy.
/// </summary>
public sealed class PhiMaskingEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var property in logEvent.Properties.ToArray())
        {
            if (!PhiRedactor.IsSensitiveName(property.Key))
            {
                continue;
            }

            var raw = property.Value is ScalarValue { Value: not null } scalar
                ? scalar.Value
                : property.Value.ToString();
            logEvent.AddOrUpdateProperty(new LogEventProperty(
                property.Key,
                new ScalarValue(PhiRedactor.Redact(property.Key, raw))));
        }

        logEvent.AddOrUpdateProperty(new LogEventProperty("phi_guard", new ScalarValue("enabled")));
    }
}
