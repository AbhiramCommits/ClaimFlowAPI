using System.Data.Common;
using ClaimFlow.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ClaimFlow");
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--connection" && i + 1 < args.Length)
    {
        connectionString = args[++i];
    }
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set ConnectionStrings__ClaimFlow or pass --connection.");
    return 1;
}

var scriptsPath = Environment.GetEnvironmentVariable("DB_SCRIPTS_PATH")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "db");

Console.WriteLine("Waiting for SQL Server...");
await WaitForServerAsync(connectionString);

var options = new DbContextOptionsBuilder<ClaimFlowDbContext>()
    .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(5))
    .Options;
await using (var db = new ClaimFlowDbContext(options))
{
    Console.WriteLine("Applying EF Core migrations...");
    await db.Database.MigrateAsync();
}

foreach (var file in new[]
         {
             "001_schema_notes.sql",
             "010_views.sql",
             "020_procs.sql",
             "030_indexes.sql",
         })
{
    var path = Path.Combine(scriptsPath, file);
    if (!File.Exists(path))
    {
        throw new FileNotFoundException($"Missing SQL script {path}", path);
    }

    Console.WriteLine($"Applying {file}...");
    var script = await File.ReadAllTextAsync(path);
    foreach (var batch in SplitOnGo(script))
    {
        if (string.IsNullOrWhiteSpace(batch))
        {
            continue;
        }

        await ExecuteAsync(connectionString, batch);
    }
}

Console.WriteLine("Migrations and reporting scripts applied.");
return 0;

static async Task WaitForServerAsync(string connectionString)
{
    var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
    builder["Database"] = "master";
    for (var attempt = 1; attempt <= 30; attempt++)
    {
        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            Console.WriteLine("SQL Server is reachable.");
            return;
        }
        catch (SqlException) when (attempt < 30)
        {
            Console.WriteLine($"  SQL Server not ready (attempt {attempt}/30), retrying...");
            await Task.Delay(2000);
        }
    }
}

static async Task ExecuteAsync(string connectionString, string sql)
{
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.CommandTimeout = 120;
    await command.ExecuteNonQueryAsync();
}

static IEnumerable<string> SplitOnGo(string script)
{
    var batch = new System.Text.StringBuilder();
    using var reader = new StringReader(script);
    while (reader.ReadLine() is { } line)
    {
        if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
        {
            yield return batch.ToString();
            batch.Clear();
            continue;
        }

        batch.AppendLine(line);
    }

    yield return batch.ToString();
}
