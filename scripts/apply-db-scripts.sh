#!/usr/bin/env bash
# Apply the idempotent T-SQL reporting layer in numeric order.
#
# Requires sqlcmd (mssql-tools18) on PATH. Credentials come from environment
# variables and are never stored in the repo.
#
#   export SQLSERVER_HOST=localhost
#   export SQLSERVER_USER=sa
#   export SQLSERVER_PASSWORD='...'
#   export SQLSERVER_DATABASE=ClaimFlow
#   ./scripts/apply-db-scripts.sh
set -euo pipefail

: "${SQLSERVER_HOST:=localhost}"
: "${SQLSERVER_USER:=sa}"
: "${SQLSERVER_PASSWORD:?Set SQLSERVER_PASSWORD}"
: "${SQLSERVER_DATABASE:=ClaimFlow}"

SQLCMD_BIN="${SQLCMD_BIN:-sqlcmd}"

for file in db/001_schema_notes.sql db/010_views.sql db/020_procs.sql db/030_indexes.sql; do
  echo "Applying ${file}"
  "${SQLCMD_BIN}" \
    -S "${SQLSERVER_HOST},1433" \
    -U "${SQLSERVER_USER}" \
    -P "${SQLSERVER_PASSWORD}" \
    -d "${SQLSERVER_DATABASE}" \
    -C -b -i "${file}"
done

echo "Reporting layer applied."
