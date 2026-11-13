using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace ClaimFlow.Security.Logging;

/// <summary>
/// Destructures objects that carry PHI field names, replacing sensitive properties with masked
/// scalar values before they reach any sink. Recurses into nested claim/patient/coverage DTOs.
/// </summary>
public sealed class PhiDestructuringPolicy : IDestructuringPolicy
{
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        out LogEventPropertyValue result)
    {
        result = null!;
        if (value is null || value is string || value.GetType().IsPrimitive)
        {
            return false;
        }

        var type = value.GetType();
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToArray();

        var isClaimFlowType = type.Namespace?.StartsWith("ClaimFlow", StringComparison.Ordinal) == true;
        var hasSensitiveProperty = properties.Any(p => PhiRedactor.IsSensitiveName(p.Name));
        if (!isClaimFlowType && !hasSensitiveProperty)
        {
            return false;
        }

        if (!hasSensitiveProperty && !properties.Any(p => p.PropertyType.Namespace?.StartsWith("ClaimFlow", StringComparison.Ordinal) == true))
        {
            return false;
        }

        var values = new List<LogEventProperty>(properties.Length);
        foreach (var property in properties)
        {
            object? raw;
            try
            {
                raw = property.GetValue(value);
            }
            catch (TargetInvocationException)
            {
                continue;
            }

            if (PhiRedactor.IsSensitiveName(property.Name))
            {
                values.Add(new LogEventProperty(
                    property.Name,
                    new ScalarValue(PhiRedactor.Redact(property.Name, raw))));
                continue;
            }

            if (raw is null || raw is string || raw.GetType().IsPrimitive || raw is decimal || raw is DateTime or DateOnly)
            {
                values.Add(new LogEventProperty(property.Name, new ScalarValue(raw)));
                continue;
            }

            values.Add(new LogEventProperty(
                property.Name,
                propertyValueFactory.CreatePropertyValue(raw, destructureObjects: true)));
        }

        result = new StructureValue(values, type.Name);
        return true;
    }
}
