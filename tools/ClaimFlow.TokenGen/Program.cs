using ClaimFlow.Security.Auth;

namespace ClaimFlow.TokenGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        var role = "claims.writer";
        var subject = "local-developer";
        var expiresMinutes = 60;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--role" when i + 1 < args.Length:
                    role = args[++i];
                    break;
                case "--subject" when i + 1 < args.Length:
                    subject = args[++i];
                    break;
                case "--expires-minutes" when i + 1 < args.Length && int.TryParse(args[i + 1], out var minutes):
                    expiresMinutes = minutes;
                    i++;
                    break;
                case "--help":
                case "-h":
                    return Usage();
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.");
                    return Usage();
            }
        }

        if (!ClaimFlowRoles.All.Contains(role))
        {
            Console.Error.WriteLine($"Unknown role '{role}'. Valid roles: {string.Join(", ", ClaimFlowRoles.All)}");
            return 1;
        }

        var signingKey = Environment.GetEnvironmentVariable("Jwt__SigningKey")
            ?? Environment.GetEnvironmentVariable("JWT__SIGNINGKEY")
            ?? string.Empty;

        if (signingKey.Length < 32)
        {
            Console.Error.WriteLine("Set Jwt__SigningKey (or JWT__SIGNINGKEY) to a secret of at least 32 characters first.");
            return 1;
        }

        var options = new JwtOptions
        {
            Issuer = Environment.GetEnvironmentVariable("Jwt__Issuer") ?? "claimflow",
            Audience = Environment.GetEnvironmentVariable("Jwt__Audience") ?? "claimflow-api",
            SigningKey = signingKey,
            LifetimeMinutes = expiresMinutes,
        };

        Console.WriteLine(JwtTokenFactory.CreateToken(
            options,
            subject,
            new[] { role },
            TimeSpan.FromMinutes(expiresMinutes)));
        return 0;
    }

    private static int Usage()
    {
        Console.WriteLine("Usage: dotnet run --project tools/ClaimFlow.TokenGen -- --role claims.writer [--subject local-dev] [--expires-minutes 60]");
        Console.WriteLine($"Roles: {string.Join(", ", ClaimFlowRoles.All)}");
        Console.WriteLine("Requires Jwt__SigningKey (>= 32 chars).");
        return 1;
    }
}
