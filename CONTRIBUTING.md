# Contributing to ClaimFlowAPI

## Prerequisites

- .NET 8 SDK (the repository's `global.json` rolls forward across 8.0 feature bands).
- Docker Desktop or Docker Engine with Compose v2 for SQL Server, RabbitMQ, and image builds.
- Optional: `dotnet-ef` (`dotnet tool install --global dotnet-ef --version 8.0.11`) and `sqlcmd` for manual database work.

## Build / test loop

```bash
dotnet restore
dotnet build
dotnet test
dotnet format ClaimFlowAPI.sln --verify-no-changes
```

`dotnet build` treats warnings as errors (`Directory.Build.props`). Run `dotnet format` (without `--verify-no-changes`) before committing if formatting drifts.

Integration tests use `WebApplicationFactory` with SQLite and `InMemoryEventBus`, so they need no external services. The SQL Server reporting test is enabled by:

```bash
export CLAIMFLOW_SQLSERVER_CONNECTION='Server=localhost,1433;Database=ClaimFlowCi;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;Encrypt=False'
dotnet test tests/ClaimFlow.SqlServer.Tests
```

## Editing the database layer

1. Change entities/configuration in `src/ClaimFlow.Data`.
2. Add or regenerate a migration:
   ```bash
   export CLAIMFLOW_DESIGN_CONNECTION='...'
   ./scripts/ef-migrations.sh add DescribeTheChange
   ./scripts/ef-migrations.sh update
   ```
3. If reporting objects change, edit the numbered files under `db/` and keep them idempotent. Apply them in order with `./scripts/apply-db-scripts.sh` or `tools/ClaimFlow.Migrator`.
4. Run the full test suite and the SQL Server test.
5. If query plans or columns change materially, rerun the benchmark and commit the regenerated `results/` files.

## Benchmark changes

```bash
dotnet build
export CLAIMFLOW_SQL_PASSWORD='...'
export RABBITMQ_PASSWORD='...'
./scripts/run-benchmark.sh
```

The harness owns `ClaimFlowBench`, ports 5080/5081, and the RabbitMQ queues. Do not run it against a database you care about. Results are written to `results/benchmark.json` and `results/REPORT.md`.

## Pull request checklist

- [ ] `dotnet build` passes with zero warnings.
- [ ] `dotnet test` passes.
- [ ] `dotnet format ClaimFlowAPI.sln --verify-no-changes` passes.
- [ ] New endpoints carry an `[Authorize(Policy = ...)]` and appear in the security matrix.
- [ ] New PHI fields are added to `PhiRedactor` and the PHI leak test.
- [ ] No secrets, real connection strings, JWTs, or `.env` files are committed.
- [ ] If measured results or image sizes changed, `README.md`, `results/benchmark.json`, and `results/REPORT.md` agree.
- [ ] Migrations and `db/*.sql` remain idempotent.

## Style

- File-scoped namespaces, 4-space C# indentation, nullable enabled.
- Money is integer cents; never store or log floating-point money.
- Use `TimeProvider` rather than reading the clock directly in request paths.
- Keep error responses as ProblemDetails and keep health endpoints anonymous.
