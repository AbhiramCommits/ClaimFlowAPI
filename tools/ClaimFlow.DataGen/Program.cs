using System.Globalization;
using System.Text.Json;
using ClaimFlow.Shared.Validation;

namespace ClaimFlow.DataGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (!GeneratorOptions.TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine(GeneratorOptions.Usage);
            return 1;
        }

        var generator = new SyntheticClaimGenerator(options);
        var generated = generator.Generate();

        var outPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

        using (var writer = new StreamWriter(outPath, append: false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (var bundle in generated.Bundles)
            {
                writer.WriteLine(JsonSerializer.Serialize(bundle, jsonOptions));
            }
        }

        PrintSummary(generated, options, outPath);
        return 0;
    }

    private static void PrintSummary(GeneratedDataSet data, GeneratorOptions options, string outPath)
    {
        Console.WriteLine($"Wrote {data.Bundles.Count:N0} FHIR-shaped claim bundles to {outPath}");
        Console.WriteLine($"seed={options.Seed} count={options.Count} error-rate={options.ErrorRate:0.###} as-of={options.AsOf:yyyy-MM-dd}");

        var injected = data.InjectedErrors.Values.Sum();
        var injectedRate = data.Bundles.Count == 0 ? 0 : (double)injected / data.Bundles.Count;
        Console.WriteLine();
        Console.WriteLine($"Injected errors: {injected:N0} of {data.Bundles.Count:N0} ({injectedRate:P2})");
        foreach (var pair in data.InjectedErrors.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {pair.Key,-24} {pair.Value,6:N0}");
        }

        var ruleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bundle in data.Bundles)
        {
            var result = ClaimValidator.Validate(bundle, options.AsOf);
            foreach (var code in result.RuleCodes)
            {
                ruleCounts[code] = ruleCounts.GetValueOrDefault(code) + 1;
            }
        }

        var rejected = data.Bundles.Count(b => !ClaimValidator.Validate(b, options.AsOf).IsValid);
        var accepted = data.Bundles.Count - rejected;
        var cleanRate = data.Bundles.Count == 0 ? 0 : (double)accepted / data.Bundles.Count;
        Console.WriteLine();
        Console.WriteLine($"Validator outcome: accepted={accepted:N0} rejected={rejected:N0} clean-claim-rate={cleanRate:P2}");
        foreach (var pair in ruleCounts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var description = ValidationRuleCodes.Descriptions.TryGetValue(pair.Key, out var d) ? d : pair.Key;
            Console.WriteLine($"  {pair.Key,-8} {pair.Value,6:N0}  {description}");
        }
    }
}

internal sealed record GeneratorOptions(int Count, double ErrorRate, int Seed, string OutputPath, DateOnly AsOf)
{
    public const string Usage =
        "Usage: dotnet run --project tools/ClaimFlow.DataGen -- --count 5000 --error-rate 0.15 --seed 42 --out data/claims.ndjson";

    public static bool TryParse(string[] args, out GeneratorOptions options, out string error)
    {
        var count = 5_000;
        var errorRate = 0.15;
        var seed = 42;
        var output = Path.Combine("data", "claims.ndjson");
        var asOf = DateOnly.FromDateTime(DateTime.UtcNow);
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--count":
                    if (!TryReadInt(args, ref i, out count)) { error = "--count requires an integer"; options = default!; return false; }
                    break;
                case "--error-rate":
                    if (!TryReadDouble(args, ref i, out errorRate)) { error = "--error-rate requires a number"; options = default!; return false; }
                    break;
                case "--seed":
                    if (!TryReadInt(args, ref i, out seed)) { error = "--seed requires an integer"; options = default!; return false; }
                    break;
                case "--out":
                    if (!TryReadString(args, ref i, out output)) { error = "--out requires a path"; options = default!; return false; }
                    break;
                case "--as-of":
                    if (!TryReadString(args, ref i, out var asOfRaw) ||
                        !DateOnly.TryParse(asOfRaw, CultureInfo.InvariantCulture, DateTimeStyles.None, out asOf))
                    {
                        error = "--as-of requires an ISO date (yyyy-MM-dd)";
                        options = default!;
                        return false;
                    }
                    break;
                case "--help":
                case "-h":
                    error = Usage;
                    options = default!;
                    return false;
                default:
                    error = $"Unknown argument '{arg}'";
                    options = default!;
                    return false;
            }
        }

        if (count <= 0 || count > 5_000_000)
        {
            error = "--count must be between 1 and 5,000,000";
            options = default!;
            return false;
        }

        if (errorRate is < 0 or > 1)
        {
            error = "--error-rate must be between 0 and 1";
            options = default!;
            return false;
        }

        options = new GeneratorOptions(count, errorRate, seed, output, asOf);
        return true;
    }

    private static bool TryReadString(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static bool TryReadInt(string[] args, ref int index, out int value)
    {
        if (!TryReadString(args, ref index, out var raw))
        {
            value = 0;
            return false;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadDouble(string[] args, ref int index, out double value)
    {
        if (!TryReadString(args, ref index, out var raw))
        {
            value = 0;
            return false;
        }

        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
