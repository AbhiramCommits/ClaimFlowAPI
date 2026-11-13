# ClaimFlowAPI benchmark report

Generated: `2026-11-13T08:46:06.4601750Z`

All values below were measured by `tools/ClaimFlow.Bench` in this run. No placeholders.

| Setting | Value |
|---|---|
| Database | SQL Server 2022 (mcr.microsoft.com/mssql/server:2022-latest, linux/amd64 container) |
| Message bus | RabbitMQ 3.13 (rabbitmq:3-management container) |
| Dataset | `data/claims.ndjson` |
| Requested claims | 5,000 |
| Ingest concurrency | 16 |

## Headline

| Metric | Value |
|---|---|
| Claims ingested | 5,000 |
| Accepted / rejected / duplicates | 4,252 / 748 / 0 |
| Clean-claim rate | 85.04% |
| Ingest throughput | 80.44 claims/sec |
| Ingest wall time | 62.16 s |
| Per-request p50 / p95 / p99 | 129.0 / 615.6 / 1,194.5 ms |
| End-to-end p50 / p95 / p99 | 44,720 / 93,682 / 108,179 ms |
| Avg / median days in A/R | 85.4 / 85.0 days |
| Open A/R at report time | 2,672 claims, $924,994.31 |
| Top denial reason | PR1 - Deductible amount (28.65%, 657 claims) |
| Denial rollup query | 44.70 ms without 030 indexes -> 39.12 ms with them (12.5% improvement) |

## Rejections by validation rule

| Rule | Description | Claims | % of submissions |
|---|---|---:|---:|
| CPT001 | Procedure code is not a valid CPT/HCPCS code | 79 | 1.58% |
| PAY001 | Payer is not in the contracted payer directory | 79 | 1.58% |
| DUP001 | Duplicate service line on the same claim | 78 | 1.56% |
| MBR001 | Member identifier is missing | 72 | 1.44% |
| CHG001 | Line or claim charge is zero or negative | 70 | 1.40% |
| POS001 | Place of service is missing or invalid | 68 | 1.36% |
| SVC001 | Service date is in the future | 64 | 1.28% |
| SVC002 | Service date is older than the payer filing limit | 64 | 1.28% |
| NPI001 | Provider NPI failed format or Luhn checksum validation | 61 | 1.22% |
| ICD001 | Diagnosis code is not a valid ICD-10-CM code | 57 | 1.14% |
| CHG002 | Line charges do not sum to the claim total | 56 | 1.12% |

## Adjudication outcome mix

| Outcome | Claims | % of accepted |
|---|---:|---:|
| PartiallyPaid | 2,714 | 63.83% |
| Denied | 886 | 20.84% |
| Paid | 652 | 15.33% |

## Top denial reasons

| Rank | Code | Description | Claims | Charge amount | % of denials |
|---:|---|---|---:|---:|---:|
| 1 | PR1 | Deductible amount | 657 | $914,085.48 | 28.65% |
| 2 | PR204 | Service not covered under the current benefit plan | 570 | $1,018,828.79 | 24.86% |
| 3 | CO50 | Non-covered because not deemed a medical necessity | 556 | $1,059,486.20 | 24.25% |
| 4 | CO27 | Expenses incurred after coverage terminated | 330 | $506,096.87 | 14.39% |
| 5 | CO97 | Benefit included in another service (bundled) | 180 | $369,198.99 | 7.85% |

## Days in A/R

As of `2026-11-13`. Open A/R includes unadjudicated claims and patient-responsibility balances on paid claims.

| Aging bucket | Claims | A/R amount |
|---|---:|---:|
| 0-30 days | 502 | $182,325.78 |
| 31-60 days | 456 | $154,811.19 |
| 61-90 days | 467 | $153,495.08 |
| 90+ days | 1,247 | $434,362.26 |

| Payer | Open claims | Open A/R | Avg days | Median days |
|---|---:|---:|---:|---:|
| Medicare | 553 | $218,062.19 | 82.9 | 83.0 |
| Cigna | 560 | $205,905.43 | 87.0 | 88.5 |
| Humana | 572 | $164,307.11 | 86.7 | 86.0 |
| Aetna | 576 | $156,625.27 | 83.1 | 81.5 |
| UnitedHealthcare | 211 | $99,853.13 | 87.1 | 86.0 |
| Blue Cross Blue Shield | 200 | $80,241.18 | 88.7 | 88.0 |

## End-to-end latency

Samples: 4,252 adjudicated claims, measured from `ReceivedAt` to `AdjudicatedAt`.

| Percentile | Latency |
|---|---:|
| p50 | 44,720 ms |
| p95 | 93,682 ms |
| p99 | 108,179 ms |
| max | 121,668 ms |

## Reporting endpoints

| Endpoint | HTTP | Rows | Warm-cache latency |
|---|---:|---:|---:|
| `/api/v1/reports/denials` | 200 | 5 | 83.33 ms |
| `/api/v1/reports/days-in-ar` | 200 | 6 | 118.52 ms |
| `/api/v1/reports/rejects` | 200 | 11 | 27.60 ms |
| `/api/v1/reports/throughput` | 200 | 1 | 64.83 ms |

## Denial rollup: index impact

`usp_DenialReasonRollup` executed five times after a warm-up, median reported. The 030 indexes were dropped and recreated between measurements.

| Measurement | Median |
|---|---:|
| Without 030 indexes | 44.70 ms |
| With 030 indexes | 39.12 ms |
| Improvement | 12.48% |

Rows returned: 5.

## Throughput by hour

| Hour (UTC) | Ingested | Adjudicated |
|---|---:|---:|
| 2026-11-13 08:00 | 5,000 | 4,252 |

## Reproduce

```bash
dotnet build
export CLAIMFLOW_SQL_PASSWORD='<local SQL Server password>'
export RABBITMQ_PASSWORD='<local RabbitMQ password>'
dotnet run --project tools/ClaimFlow.Bench -- --claims 5000 --concurrency 16
```

The harness resets the database, applies EF migrations and `db/*.sql`, purges the RabbitMQ queues, starts both services, ingests the dataset over HTTP, waits for full adjudication, calls every report endpoint, and times the rollup with and without the reporting indexes.
