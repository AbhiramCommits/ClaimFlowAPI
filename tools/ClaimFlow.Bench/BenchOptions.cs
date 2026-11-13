namespace ClaimFlow.Bench;

public sealed record BenchOptions(
    int Claims,
    int Concurrency,
    string DataPath,
    string OutputDirectory,
    string ConnectionString,
    string RabbitHost,
    int RabbitPort,
    string RabbitUser,
    string RabbitPassword,
    string IntakeUrl,
    string AdjudicationUrl,
    bool SkipStart,
    bool SkipReset,
    string RepositoryRoot)
{
    public const string Usage =
        "Usage: dotnet run --project tools/ClaimFlow.Bench -- --claims 5000 --concurrency 16 " +
        "[--sql-connection ...] [--sql-password ...] [--rabbit-host localhost] [--rabbit-port 5672] " +
        "[--rabbit-user guest] [--rabbit-password ...] [--intake-url http://127.0.0.1:5080] " +
        "[--adjudication-url http://127.0.0.1:5081] [--skip-start] [--skip-reset]";

    public static bool TryParse(string[] args, out BenchOptions options, out string error)
    {
        var claims = 5000;
        var concurrency = 16;
        var data = "data/claims.ndjson";
        var output = "results";
        string? connection = null;
        var sqlPassword = Environment.GetEnvironmentVariable("CLAIMFLOW_SQL_PASSWORD") ?? string.Empty;
        var rabbitHost = Environment.GetEnvironmentVariable("RABBITMQ_HOST") ?? "localhost";
        var rabbitPort = int.TryParse(Environment.GetEnvironmentVariable("RABBITMQ_PORT"), out var rp) ? rp : 5672;
        var rabbitUser = Environment.GetEnvironmentVariable("RABBITMQ_USER") ?? "guest";
        var rabbitPassword = Environment.GetEnvironmentVariable("RABBITMQ_PASSWORD") ?? "guest";
        var intakeUrl = "http://127.0.0.1:5080";
        var adjudicationUrl = "http://127.0.0.1:5081";
        var skipStart = false;
        var skipReset = false;
        error = string.Empty;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--claims" when i + 1 < args.Length && int.TryParse(args[++i], out var parsedClaims):
                    claims = parsedClaims;
                    break;
                case "--concurrency" when i + 1 < args.Length && int.TryParse(args[++i], out var parsedConcurrency):
                    concurrency = parsedConcurrency;
                    break;
                case "--data" when i + 1 < args.Length:
                    data = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--sql-connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                case "--sql-password" when i + 1 < args.Length:
                    sqlPassword = args[++i];
                    break;
                case "--rabbit-host" when i + 1 < args.Length:
                    rabbitHost = args[++i];
                    break;
                case "--rabbit-port" when i + 1 < args.Length && int.TryParse(args[++i], out var parsedRp):
                    rabbitPort = parsedRp;
                    break;
                case "--rabbit-user" when i + 1 < args.Length:
                    rabbitUser = args[++i];
                    break;
                case "--rabbit-password" when i + 1 < args.Length:
                    rabbitPassword = args[++i];
                    break;
                case "--intake-url" when i + 1 < args.Length:
                    intakeUrl = args[++i];
                    break;
                case "--adjudication-url" when i + 1 < args.Length:
                    adjudicationUrl = args[++i];
                    break;
                case "--skip-start":
                    skipStart = true;
                    break;
                case "--skip-reset":
                    skipReset = true;
                    break;
                case "--help":
                case "-h":
                    error = Usage;
                    options = null!;
                    return false;
                default:
                    error = $"Unknown or incomplete argument '{args[i]}'.";
                    options = null!;
                    return false;
            }
        }

        if (claims <= 0 || concurrency <= 0)
        {
            error = "--claims and --concurrency must be positive.";
            options = null!;
            return false;
        }

        connection ??= BuildConnectionString(sqlPassword);
        if (string.IsNullOrWhiteSpace(connection) || connection.Contains("__SET_VIA_ENV__", StringComparison.Ordinal))
        {
            error = "Provide --sql-connection or CLAIMFLOW_SQL_PASSWORD to build the SQL Server connection string.";
            options = null!;
            return false;
        }

        var root = FindRepositoryRoot(Directory.GetCurrentDirectory())
            ?? FindRepositoryRoot(AppContext.BaseDirectory);
        if (root is null)
        {
            error = "Could not locate ClaimFlowAPI.sln; run the bench from the repository root.";
            options = null!;
            return false;
        }

        options = new BenchOptions(
            claims, concurrency,
            Path.GetFullPath(Path.Combine(root, data)),
            Path.GetFullPath(Path.Combine(root, output)),
            connection,
            rabbitHost, rabbitPort, rabbitUser, rabbitPassword,
            intakeUrl.TrimEnd('/'), adjudicationUrl.TrimEnd('/'),
            skipStart, skipReset, root);
        return true;
    }

    private static string BuildConnectionString(string password) =>
        string.IsNullOrWhiteSpace(password)
            ? string.Empty
            : $"Server=localhost,1433;Database=ClaimFlowBench;User Id=sa;Password={password};TrustServerCertificate=True;Encrypt=False;Max Pool Size=100";

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ClaimFlowAPI.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    public string RedactedConnectionString()
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = ConnectionString };
        if (builder.ContainsKey("Password"))
        {
            builder["Password"] = "***";
        }

        return builder.ConnectionString;
    }
}
