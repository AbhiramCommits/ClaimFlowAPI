# ClaimFlowAPI

Cloud-native healthcare claims processing built as two ASP.NET Core 8 microservices, with a SQL Server reporting layer, RabbitMQ messaging, JWT role-based authorization, PHI-safe structured logging, Docker packaging, and a measured benchmark harness.

The system takes a claim from **intake → validation → queue → eligibility/adjudication → denial coding → reporting**. It is framed around the revenue-cycle metrics a claims operation actually tracks:

- **Clean-claim rate** — what percentage of submitted claims survive validation.
- **Denial reasons** — which CARC/RARC-style codes drive denials, and how much charge they represent.
- **Days in A/R** — how long open receivables sit by aging bucket.
- **Throughput and latency** — claims ingested per second and time from intake to adjudication.

> All data in this repository is synthetic. `data/claims.ndjson` was generated with a fixed seed and contains no real PHI.

---

## 1. What it does

1. **Intake** accepts a FHIR-shaped `ClaimBundle` (Patient + Coverage + Claim resources) over REST, with either a single claim or an NDJSON/JSON-array batch.
2. Eleven validation rules check member identity, payer contract, service dates and timely filing, NPI Luhn checksum, ICD-10/CPT formats, charges, line totals, duplicate lines, and place of service.
3. Valid claims are normalized to canonical domain records, money becomes integer cents, and the claim is persisted with lines, patient, status history, and a **transactional outbox event** in one SQL Server transaction.
4. Rejected claims are persisted too, with their rule codes, so rejection reporting is grounded in real submissions rather than an in-memory counter.
5. A background outbox publisher sends `claim.submitted` to RabbitMQ. **Adjudication** consumes it, checks eligibility against a deterministic synthetic coverage store, runs the rules engine, assigns `Paid` / `PartiallyPaid` / `Denied` and a denial reason code, reports the result back to Intake over HTTP, and publishes `claim.adjudicated`.
6. A T-SQL reporting layer (views, stored procedures, covering indexes) produces denial rollups, days-in-A/R aging, validation-reject summaries, and hourly throughput. The same endpoints have a LINQ fallback for SQLite.

---

## 2. Architecture

```
Provider / EDI / test data
        │  POST /api/claims (JWT claims.writer)
        ▼
┌──────────────────────────────┐        ┌──────────────────────────────┐
│ ClaimFlow.Intake             │        │ ClaimFlow.Adjudication       │
│  validation (11 rules)       │        │  claim.submitted consumer    │
│  normalization               │        │  synthetic eligibility store │
│  EF Core → SQL Server        │        │  adjudication rules engine    │
│  status history              │        │  denial reason coding        │
│  transactional outbox        │        │  typed HttpClient + Polly    │
└──────────────┬───────────────┘        └──────────────┬───────────────┘
               │ outbox publisher                       │ PUT /status (JWT adjudicator)
               ▼                                        │
┌──────────────────────────────┐                        │
│ RabbitMQ topic exchange      │                        │
│  claimflow                   │  claim.submitted       │
│   ├─ claim.adjudicated       │────────────────────────┘
│   └─ claim.dead-letter       │
└──────────────┬───────────────┘
               │ consumer (retry + exponential backoff + DLQ)
               ▼
        adjudication results
               │
               ▼
┌─────────────────────────────────────────────────────────────────────┐
│ SQL Server 2022                                                      │
│  Claims / ClaimLines / Patients / Payers / ClaimStatusHistory        │
│  OutboxMessages / ProcessedEvents                                    │
│  vw_ClaimFinancials (aging buckets)                                  │
│  vw_ClaimDenials                                                     │
│  usp_DenialReasonRollup / usp_DaysInAR                               │
│  usp_ValidationRejectSummary / usp_ClaimThroughputByHour             │
│  030 reporting indexes                                               │
└──────────────────────────────┬──────────────────────────────────────┘
                               │ /api/v1/reports/* (JWT reporting.reader)
                               ▼
                    denial / A/R / reject / throughput JSON
```

Cross-cutting: JWT bearer auth on every non-health endpoint, correlation IDs (`X-Correlation-Id`) propagated into Serilog and message headers, PHI redaction before every sink, and ProblemDetails error responses.

### Project map

| Project | Kind | Responsibility |
|---|---|---|
| `src/ClaimFlow.Shared` | library | FHIR-shaped DTOs, canonical domain records, enums, payer directory, validator, normalizer |
| `src/ClaimFlow.Data` | library | EF Core 8 `ClaimFlowDbContext`, entities, configurations, SQL Server migrations |
| `src/ClaimFlow.Intake` | ASP.NET Core API | REST intake, batch ingest, status updates, reports, outbox publisher |
| `src/ClaimFlow.Adjudication` | ASP.NET Core API | eligibility checks, dry-run adjudication, queue consumer, status callback |
| `src/ClaimFlow.Messaging` | library | `IClaimEventBus`, RabbitMQ topic bus with confirms/retries/DLQ, in-memory channel bus |
| `src/ClaimFlow.Security` | library | JWT setup, role policies, service-to-service token, correlation middleware, Serilog + PHI redaction |
| `tools/ClaimFlow.DataGen` | console | deterministic synthetic FHIR dataset with injected validation errors |
| `tools/ClaimFlow.TokenGen` | console | mints role-scoped JWTs for local commands |
| `tools/ClaimFlow.Bench` | console | resets the stack, ingests over HTTP, drains adjudication, calls reports, times indexes, writes results |
| `tools/ClaimFlow.Migrator` | console | applies EF migrations and `db/*.sql` in order; used by Compose and CI |
| `tests/ClaimFlow.Shared.Tests` | xUnit | every validation rule, normalization, money and code handling |
| `tests/ClaimFlow.Intake.Tests` | xUnit | intake API, batch, duplicate detection, pagination, health, reports via SQLite |
| `tests/ClaimFlow.Adjudication.Tests` | xUnit | eligibility states and every adjudication/denial rule |
| `tests/ClaimFlow.Integration.Tests` | xUnit | both `WebApplicationFactory` hosts over `InMemoryEventBus`; full status flow and idempotency |
| `tests/ClaimFlow.Security.Tests` | xUnit | authorization matrix, endpoint metadata coverage, PHI leak tests |
| `tests/ClaimFlow.SqlServer.Tests` | xUnit | optional real SQL Server reporting procedure test, enabled by environment |

### Event contract

Topic exchange: `claimflow` (durable). Routing keys:

| Routing key | Producer | Consumer | Queue |
|---|---|---|---|
| `claim.submitted` | Intake outbox publisher | Adjudication consumer | `claimflow.adjudication` (durable) |
| `claim.adjudicated` | Adjudication | future consumers | exchange only today |
| `claim.dead-letter` | RabbitMQ bus after retry exhaustion | operations | `claimflow.dead-letter` (durable) |

Envelope:

```json
{
  "eventId": "b0d5c4c0-6c25-4c19-9f4b-0f6f1b0e5e11",
  "eventType": "claim.submitted",
  "correlationId": "0HNQ1C5FLVK8R:00000001",
  "occurredAt": "2026-11-13T08:44:00Z",
  "data": {
    "eventId": "b0d5c4c0-6c25-4c19-9f4b-0f6f1b0e5e11",
    "claimId": "CLM-42-000123",
    "payerId": "PAYER-BCBS",
    "memberId": "MBR-000123",
    "providerNpi": "1234567893",
    "placeOfService": "11",
    "serviceDate": "2026-11-02",
    "totalChargeCents": 15000,
    "lines": [
      {
        "lineNumber": 1,
        "cptCode": "99213",
        "icd10Code": "J45.909",
        "placeOfService": "11",
        "chargeCents": 15000,
        "units": 1
      }
    ],
    "correlationId": "0HNQ1C5FLVK8R:00000001",
    "occurredAt": "2026-11-13T08:44:00Z"
  }
}
```

`claim.adjudicated` `data` carries `claimId`, `payerId`, `memberId`, `outcome`, `denialReasonCode`, `allowedCents`, `paidCents`, `patientResponsibilityCents`, `correlationId`, and `adjudicatedAt`.

Delivery is **at-least-once**: Intake writes the outbox row in the same transaction as the claim, publishes with publisher confirms, then marks the row processed. Adjudication records `(eventId)` in `ProcessedEvents` and Intake's status endpoint is idempotent on the same event id. RabbitMQ consumer failures retry with exponential backoff and land in `claimflow.dead-letter`. See [`docs/adr/0001-outbox-and-messaging.md`](docs/adr/0001-outbox-and-messaging.md).

### Endpoints and required roles

Roles: `claims.writer`, `adjudicator`, `reporting.reader`, `admin`. `admin` passes every policy.

| Service | Method & path | Policy / role | Notes |
|---|---|---|---|
| Intake | `POST /api/claims` (alias `/api/v1/claims`) | `ClaimsWrite` — `claims.writer` | 201 accepted, 422 rejected (persisted), 409 duplicate |
| Intake | `POST /api/claims/batch` (alias `/api/v1/claims/batch`) | `ClaimsWrite` — `claims.writer` | NDJSON or JSON array; per-record results |
| Intake | `GET /api/claims/{claimId}` | `ClaimsRead` — writer/adjudicator/reporting.reader | Claim + lines + status history |
| Intake | `GET /api/claims?status=&payer=&page=&pageSize=` | `ClaimsRead` | Paged summaries |
| Intake | `PUT /api/claims/{claimId}/status` | `Adjudicate` — `adjudicator` | Idempotent by `eventId`; appends Adjudicated + final history |
| Intake | `GET /api/v1/reports/denials` | `ReportingRead` | `fromDate`, `toDate`, `payerId` |
| Intake | `GET /api/v1/reports/days-in-ar` | `ReportingRead` | `asOfDate` |
| Intake | `GET /api/v1/reports/rejects` | `ReportingRead` | `fromDate`, `toDate` |
| Intake | `GET /api/v1/reports/throughput` | `ReportingRead` | hourly ingest vs adjudication |
| Intake | `GET /health/live`, `/health/ready` | anonymous | readiness includes the database check |
| Adjudication | `POST /api/v1/eligibility/check` | `Adjudicate` — `adjudicator` | Active / Termed / DeductibleRemaining |
| Adjudication | `POST /api/v1/adjudication/dry-run` | `Adjudicate` — `adjudicator` | synchronous engine result, no persistence |
| Adjudication | `GET /health/live`, `/health/ready` | anonymous | readiness includes the database check |

Swagger is enabled in Development at `http://localhost:5080/swagger` and `http://localhost:5081/swagger`. Missing/invalid JWT returns **401**; a valid JWT without the required role returns **403**.

---

## 3. How to run

Prerequisites: .NET 8 SDK, Docker Desktop (or Docker Engine + Compose v2). On Apple Silicon the SQL Server 2022 image runs as `linux/amd64` under emulation and may take a minute to become healthy.

### (a) Docker Compose quickstart

```bash
cp .env.example .env
# edit .env and replace all three placeholders, then:
docker compose up --build
```

The stack starts SQL Server, RabbitMQ, the migrator (EF migrations + all `db/*.sql` scripts in order), Intake, and Adjudication. The service containers become healthy and stay up.

```bash
curl -fsS http://localhost:5080/health/ready
curl -fsS http://localhost:5081/health/ready
curl -fsS http://localhost:5080/swagger/claims/swagger.json > /dev/null
```

Host ports are configurable in `.env` (`SQLSERVER_HOST_PORT`, `RABBITMQ_HOST_PORT`, `RABBITMQ_MANAGEMENT_HOST_PORT`, `INTAKE_HOST_PORT`, `ADJUDICATION_HOST_PORT`). The RabbitMQ management UI is at `http://localhost:15672` (or the configured management port) using the credentials from `.env`.

### (b) Local `dotnet run`

Start SQL Server and RabbitMQ (Compose is convenient):

```bash
docker compose up -d sqlserver rabbitmq
export ASPNETCORE_ENVIRONMENT=Development
export Database__Provider=SqlServer
export ConnectionStrings__ClaimFlow='Server=localhost,1433;Database=ClaimFlow;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;Encrypt=False'
export Messaging__Provider=RabbitMq
export RabbitMq__HostName=localhost
export RabbitMq__Port=5672
export RabbitMq__UserName=claimflow
export RabbitMq__Password=YOUR_PASSWORD
export Jwt__SigningKey='YOUR_AT_LEAST_32_CHARACTER_SECRET'
```

Terminal 1:

```bash
dotnet run --project src/ClaimFlow.Intake
```

Terminal 2:

```bash
dotnet run --project src/ClaimFlow.Adjudication
```

The launch profiles bind Intake on `http://localhost:5080` and Adjudication on `http://localhost:5081`. Apply the schema before the first request (next section).

SQLite fallback uses the same API surface and LINQ reporting:

```bash
export Database__Provider=Sqlite
export ConnectionStrings__ClaimFlow='Data Source=./claimflow.db'
export Messaging__Provider=InMemory
```

The in-memory bus is process-local, so run only one service for ad-hoc API work or rely on the integration tests/benchmark for a full two-service flow. SQLite has no stored procedures; the report endpoints use `SqliteReportingQuery`.

### (c) Migrations and `db/*.sql` order

Either run the migrator once (it is what Compose and CI use):

```bash
dotnet run --project tools/ClaimFlow.Migrator -- \
  --connection 'Server=localhost,1433;Database=ClaimFlow;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;Encrypt=False'
```

or run the pieces explicitly:

```bash
dotnet tool install --global dotnet-ef --version 8.0.11
export CLAIMFLOW_DESIGN_CONNECTION='Server=localhost,1433;Database=ClaimFlow;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;Encrypt=False'
./scripts/ef-migrations.sh update
export SQLSERVER_PASSWORD='YOUR_PASSWORD'
./scripts/apply-db-scripts.sh
```

Script order is always:

1. EF Core migration `InitialCreate` (tables, indexes, seeded payers)
2. `db/001_schema_notes.sql` (documentation/no-op)
3. `db/010_views.sql` (`vw_ClaimFinancials`, `vw_ClaimDenials`)
4. `db/020_procs.sql` (four reporting procedures)
5. `db/030_indexes.sql` (covering indexes)

All scripts are idempotent (`CREATE OR ALTER` / `IF NOT EXISTS`).

### (d) Generating the synthetic dataset

```bash
dotnet run --project tools/ClaimFlow.DataGen -- \
  --count 5000 --error-rate 0.15 --seed 42 --out data/claims.ndjson
```

The committed dataset was generated with this command, seed 42, 5,000 records, 748 injected errors (14.96%), and 4,252 validator-clean claims. The generator prints a summary by injected error type and by validation rule code.

### (e) Minting a JWT

```bash
export Jwt__SigningKey='YOUR_AT_LEAST_32_CHARACTER_SECRET'
export Jwt__Issuer=claimflow
export Jwt__Audience=claimflow-api
TOKEN=$(dotnet run --project tools/ClaimFlow.TokenGen -- --role claims.writer)
curl -H "Authorization: Bearer $TOKEN" http://localhost:5080/api/claims?page=1
```

Valid roles: `claims.writer`, `adjudicator`, `reporting.reader`, `admin`. The signing key is read from the environment (`Jwt__SigningKey` or `JWT__SIGNINGKEY`) and is never committed. The services fail closed if the key is missing or shorter than 32 bytes.

### (f) Running the benchmark

The harness expects a reachable SQL Server and RabbitMQ, resets its own `ClaimFlowBench` database, applies migrations and `db/*.sql`, purges the queues, starts both services as child processes, ingests over real HTTP, waits for full adjudication, calls every report endpoint, times the denial rollup with and without the 030 indexes, and writes `results/benchmark.json` plus `results/REPORT.md`.

```bash
dotnet build
export CLAIMFLOW_SQL_PASSWORD='YOUR_PASSWORD'
export RABBITMQ_PASSWORD='YOUR_PASSWORD'
export RABBITMQ_PORT=5672
./scripts/run-benchmark.sh
# or directly:
dotnet run --project tools/ClaimFlow.Bench -- --claims 5000 --concurrency 16
```

Ensure ports 5080/5081 are free (stop Compose or other local services first). Use `--rabbit-port`, `--sql-connection`, `--intake-url`, and `--adjudication-url` to point at non-default infrastructure. The results committed here were produced with the command above and RabbitMQ on host port 5673 only because another local project occupied 5672.

### (g) Tests and coverage

```bash
dotnet test
dotnet test --collect:"XPlat Code Coverage" --results-directory TestResults
dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:coverage -reporttypes:"TextSummary;Html"
cat coverage/Summary.txt
```

The real-T-SQL test runs when a SQL Server connection is supplied (CI does this against its service container):

```bash
export CLAIMFLOW_SQLSERVER_CONNECTION='Server=localhost,1433;Database=ClaimFlowCi;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;Encrypt=False'
dotnet test tests/ClaimFlow.SqlServer.Tests
```

Formatting must stay clean:

```bash
dotnet format ClaimFlowAPI.sln --verify-no-changes
```

### (h) CI/CD pipelines

`.github/workflows/ci.yml` runs on every push/PR:

1. restore and build in Release with warnings as errors;
2. `dotnet test` with Coverlet coverage, a ReportGenerator summary in the job output, and uploaded TRX/coverage artifacts;
3. the real SQL Server reporting test against a SQL Server 2022 service container;
4. a separate `sql-lint` job that applies EF migrations and executes every `db/*.sql` script against a SQL Server service container;
5. Docker builds for intake, adjudication, and migrator plus a `docker compose config` validation.

`.github/workflows/deploy.yml` runs on `v*` tags or manual dispatch. It builds and pushes three images to GHCR, then updates Azure Container Apps revisions. The deploy job is skipped when `AZURE_CREDENTIALS` is not configured (a preflight job checks the secret without printing it), and after deployment it gates on both apps' `/health/ready` endpoints. Required repository secrets for a real deployment:

| Secret | Purpose |
|---|---|
| `AZURE_CREDENTIALS` | Azure service-principal JSON for `azure/login`; absent ⇒ deploy no-ops |
| `AZURE_RESOURCE_GROUP` | Resource group containing the Container Apps |
| `AZURE_INTAKE_APP` | Container App name for Intake |
| `AZURE_ADJUDICATION_APP` | Container App name for Adjudication |

The Container Apps must already exist, have GHCR pull credentials configured, and have their database/RabbitMQ/JWT environment variables set. The workflow updates images and revisions; it does not provision infrastructure. CI's SQL service password is generated per run from `github.run_id`; there is no committed CI database password.

---

## 4. Results

**Backend for every runtime number below:** SQL Server 2022 (`mcr.microsoft.com/mssql/server:2022-latest`, `linux/amd64` container under Docker Desktop on arm64) and RabbitMQ 3.13 (`rabbitmq:3-management` container). The harness started both services itself and drove them over HTTP with 16 concurrent clients. Test/coverage numbers come from a real `dotnet test` run that executed the SQL Server test against SQL Server 2022; image sizes come from `docker inspect` on the locally built images.

Measured run: `results/benchmark.json` generated `2026-11-13T08:46:06Z`.

| Metric | Measured value |
|---|---:|
| Claims ingested | 5,000 |
| Accepted / rejected / duplicates | 4,252 / 748 / 0 |
| Clean-claim rate | **85.04%** |
| Ingest wall time | 62.16 s |
| Ingest throughput | **80.44 claims/sec** |
| Per-request p50 / p95 / p99 | 128.97 / 615.64 / 1,194.47 ms |
| End-to-end p50 / p95 / p99 (intake → adjudicated) | **44,720 / 93,682 / 108,179 ms** |
| End-to-end average / samples | 46,403.5 ms / 4,252 claims |
| Open A/R at report time | 2,672 claims / **$924,994.31** |
| Average / median days in A/R | **85.37 / 85.0 days** |
| Denial rollup, without → with 030 indexes | **44.70 ms → 39.12 ms (12.48% faster)** |
| xUnit tests | **158 passed, 0 failed** |
| Line / branch coverage | **79.0% / 68.4%** |
| Docker image size: intake / adjudication / migrator | **426.6 MB / 427.1 MB / 380.3 MB** |

The end-to-end latency is queue-inclusive: 4,252 events drained through a 4-concurrent consumer while ingestion was still running. It is not the latency of a single warm API call (per-request API latency is the row above).

### Rejections by validation rule

| Rule | Description | Claims | % of 5,000 submissions |
|---|---|---:|---:|
| CPT001 | Procedure code is not a valid CPT/HCPCS code | 79 | 1.58% |
| PAY001 | Payer is not in the contracted payer directory | 79 | 1.58% |
| DUP001 | Duplicate service line on the same claim | 78 | 1.56% |
| MBR001 | Member identifier is missing | 72 | 1.44% |
| CHG001 | Line or claim charge is zero or negative | 70 | 1.40% |
| POS001 | Place of service is missing or invalid | 68 | 1.36% |
| SVC001 | Service date is in the future | 64 | 1.28% |
| SVC002 | Service date is older than the payer filing limit | 64 | 1.28% |
| NPI001 | Provider NPI failed format or Luhn checksum | 61 | 1.22% |
| ICD001 | Diagnosis code is not a valid ICD-10-CM code | 57 | 1.14% |
| CHG002 | Line charges do not sum to the claim total | 56 | 1.12% |

### Adjudication outcome mix (4,252 accepted claims)

| Outcome | Claims | % of accepted |
|---|---:|---:|
| PartiallyPaid | 2,714 | 63.83% |
| Denied | 886 | 20.84% |
| Paid | 652 | 15.33% |

### Top denial reasons

| Rank | Code | Description | Claims | Charge amount | % of denials |
|---:|---|---|---:|---:|---:|
| 1 | PR1 | Deductible amount | 657 | $914,085.48 | **28.65%** |
| 2 | PR204 | Service not covered under the current benefit plan | 570 | $1,018,828.79 | 24.86% |
| 3 | CO50 | Non-covered because not deemed a medical necessity | 556 | $1,059,486.20 | 24.25% |
| 4 | CO27 | Expenses incurred after coverage terminated | 330 | $506,096.87 | 14.39% |
| 5 | CO97 | Benefit included in another service (bundled) | 180 | $369,198.99 | 7.85% |

### Days in A/R

As of `2026-11-13`. Open A/R includes unadjudicated claims plus patient-responsibility balances on paid claims.

| Aging bucket | Claims | A/R amount |
|---|---:|---:|
| 0–30 days | 502 | $182,325.78 |
| 31–60 days | 456 | $154,811.19 |
| 61–90 days | 467 | $153,495.08 |
| 90+ days | 1,247 | $434,362.26 |

### Report endpoint timings

| Endpoint | HTTP | Rows | Warm-cache latency |
|---|---:|---:|---:|
| `/api/v1/reports/denials` | 200 | 5 | 83.33 ms |
| `/api/v1/reports/days-in-ar` | 200 | 6 | 118.52 ms |
| `/api/v1/reports/rejects` | 200 | 11 | 27.60 ms |
| `/api/v1/reports/throughput` | 200 | 1 | 64.83 ms |

Reproduce the entire results section with the benchmark command in §3(f). `results/REPORT.md` is written by the same run and contains the same measured values.

---

## 5. Security and PHI handling

### Roles and policies

| Policy | Allowed roles | Used by |
|---|---|---|
| `ClaimsWrite` | `claims.writer`, `admin` | single and batch intake |
| `ClaimsRead` | `claims.writer`, `adjudicator`, `reporting.reader`, `admin` | claim detail/list |
| `Adjudicate` | `adjudicator`, `admin` | status updates, eligibility, dry-run |
| `ReportingRead` | `reporting.reader`, `admin` | all four report endpoints |
| `AdminOnly` | `admin` | available for administrative endpoints |

JWT bearer auth is configured identically in both services. `admin` is treated as a super-role by every policy, not by implicit role inheritance. Anonymous requests get **401**; authenticated requests without the needed role get **403**. Health endpoints are explicitly anonymous. The signing key comes from `Jwt__SigningKey` / `JWT__SIGNINGKEY`; `appsettings.json` contains only an empty placeholder. Adjudication mints a short-lived `adjudicator` service token for its Intake status callback.

`tests/ClaimFlow.Security.Tests` contains:

- a **75-case authorization matrix** (15 endpoints × 5 actors: anonymous, each role, admin) asserting 200/201/401/403 as appropriate;
- a metadata test that enumerates every non-health route and fails if an `[Authorize]` policy is missing;
- PHI leak tests and the redactor unit tests.

### PHI-safe logging

Serilog emits compact JSON to the console. Every event passes through:

- `ClaimFlowSerilog` configuration with `Enrich.FromLogContext()`;
- `PhiDestructuringPolicy` (`IDestructuringPolicy`) — detects ClaimFlow DTOs and replaces sensitive properties before they are structured;
- `PhiMaskingEnricher` (`ILogEventEnricher`) — masks scalar properties pushed via `LogContext` and adds `phi_guard=enabled`.

`PhiRedactor` centrally declares the sensitive field list: **MemberId, SSN/SocialSecurityNumber, DateOfBirth/DOB, GivenName/FirstName, FamilyName/LastName, Address/AddressLine1/Line1/PostalCode, and raw request bodies**. Masking:

| Field | Stored in logs as |
|---|---|
| MemberId | last four only, e.g. `****7654` |
| SSN | `***-**-9876` |
| DOB | `DOB#<SHA-256 prefix>` |
| Names | first initial only, e.g. `Z***` |
| Address | `***REDACTED-ADDRESS***` |
| Raw body | `sha256:<32 hex>` |

`X-Correlation-Id` is read or generated per request, echoed on the response, pushed into Serilog, carried on outbox rows, and set in RabbitMQ message headers/properties. Request bodies are never logged raw.

The PHI leak test performs a full ingest carrying known raw member id, given/family name, SSN, DOB, and address, then logs the bundle and an exception through the production Serilog configuration with an in-memory sink, and asserts none of the raw values appear. I temporarily removed the destructuring policy and enricher, confirmed the test fails with raw values present, then restored them and confirmed green.

No secrets are committed: `.env` is ignored, `.env.example` contains placeholders, test signing keys are generated randomly at runtime, and CI derives its ephemeral SQL Server password from `github.run_id`.

---

## 6. Limitations and next steps

- **Synthetic data only.** The generator is deterministic and useful for pipeline testing, but it is not clinical data and the denial/eligibility rules are simplified.
- **Shared SQL Server database.** Intake and Adjudication intentionally share one database for the transactional outbox, status reporting, and reporting procs. That is easy to operate but couples schema evolution; a production design would likely split databases and use a proper event store/outbox per service.
- **Synthetic eligibility.** The coverage store is an in-process deterministic hash (active/termed/deductible). A real deployment would call a payer eligibility API (270/271) and persist coverage periods.
- **A/R definition.** Open A/R counts unadjudicated charges plus patient-responsibility balances on paid claims; it is not a payer-contractual A/R ledger and does not model secondary payers, appeals, or refunds.
- **At-least-once delivery.** Duplicates are suppressed by `ProcessedEvents` and the status endpoint event id, but outbox pruning, poison-message replay tooling, and per-claim ordering are not implemented.
- **Single publisher/consumer process assumptions.** The outbox publisher and in-process eligibility store are not distributed. RabbitMQ itself can be clustered, but scale-out needs coordination.
- **SQLite fallback is approximate.** Reports use LINQ and do not execute the T-SQL procedures; only the DTO contract is asserted between providers. Use SQL Server for the real reporting path.
- **Coverage gaps.** `RabbitMqEventBus` is exercised end-to-end by the benchmark but has low unit coverage (9.4% of that class); retry/DLQ behavior deserves dedicated fault-injection tests.
- **Deployment scope.** `deploy.yml` updates existing Azure Container Apps images and gates on `/health/ready`; it does not provision the resource group, SQL Server, RabbitMQ, managed identities, GHCR pull credentials, or secrets. Add Azure Key Vault/Managed Identity and a managed SQL/RabbitMQ (or Service Bus) plan before production.
- **No payment posting.** The service reports adjudication results and patient responsibility but does not generate 835 remittances, post payments, or manage appeals.

Next steps: replace synthetic eligibility with a payer adapter, add OpenTelemetry traces/metrics, run k6 load tests, move secrets to Key Vault, split the databases, add outbox retention and DLQ replay tooling, and add contract tests against a real clearinghouse sandbox.

---

## License

MIT — see [LICENSE](LICENSE).
