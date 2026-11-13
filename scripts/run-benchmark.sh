#!/usr/bin/env bash
# Run the full measured benchmark against SQL Server + RabbitMQ.
#
# The harness resets the ClaimFlowBench database, applies migrations and db/*.sql,
# purges the queues, starts both services, ingests data/claims.ndjson with the
# requested concurrency, waits for adjudication, calls every reporting endpoint,
# times the denial rollup with/without the 030 indexes, and writes results/.
#
#   dotnet build
#   export CLAIMFLOW_SQL_PASSWORD='...'
#   export RABBITMQ_PASSWORD='...'
#   ./scripts/run-benchmark.sh
set -euo pipefail

: "${CLAIMFLOW_SQL_PASSWORD:?Set CLAIMFLOW_SQL_PASSWORD for SQL Server}"
: "${RABBITMQ_PASSWORD:?Set RABBITMQ_PASSWORD for RabbitMQ}"

export PATH="$HOME/.dotnet:$PATH"

dotnet run --project tools/ClaimFlow.Bench -- \
  --claims "${CLAIMS:-5000}" \
  --concurrency "${CONCURRENCY:-16}" \
  --rabbit-port "${RABBITMQ_PORT:-5672}" \
  --rabbit-user "${RABBITMQ_USER:-guest}" \
  "$@"
