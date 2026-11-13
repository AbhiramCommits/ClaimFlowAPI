using Microsoft.EntityFrameworkCore;

namespace ClaimFlow.Data;

public static class DatabaseProviderNames
{
    public const string SqlServer = "SqlServer";
    public const string Sqlite = "Sqlite";
}

public static class ClaimFlowDbContextOptionsExtensions
{
    public static DbContextOptionsBuilder UseClaimFlowDatabase(
        this DbContextOptionsBuilder builder,
        string provider,
        string connectionString)
    {
        return string.Equals(provider, DatabaseProviderNames.Sqlite, StringComparison.OrdinalIgnoreCase)
            ? builder.UseSqlite(connectionString)
            : builder.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(3);
                sql.CommandTimeout(60);
            });
    }

    public static bool IsSqlite(this DbContext context) =>
        context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;
}
