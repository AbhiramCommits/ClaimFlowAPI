using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClaimFlow.Data;

/// <summary>Used only by `dotnet ef` at design time; generating migrations does not open a connection.</summary>
public sealed class ClaimFlowDbContextFactory : IDesignTimeDbContextFactory<ClaimFlowDbContext>
{
    public ClaimFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("CLAIMFLOW_DESIGN_CONNECTION") ??
            "Server=localhost,1433;Database=ClaimFlow;User Id=sa;Password=__SET_VIA_ENV__;TrustServerCertificate=True;Encrypt=False";

        var options = new DbContextOptionsBuilder<ClaimFlowDbContext>();
        options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure(3));
        return new ClaimFlowDbContext(options.Options);
    }
}
