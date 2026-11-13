#!/usr/bin/env bash
# EF Core migration workflow for ClaimFlowAPI.
#
# Prerequisites:
#   dotnet tool install --global dotnet-ef --version 8.0.11
#
# The design-time factory reads CLAIMFLOW_DESIGN_CONNECTION (falling back to
# ConnectionStrings__ClaimFlow). Generating a migration does not open a database
# connection; `database update` does.
set -euo pipefail

export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export CLAIMFLOW_DESIGN_CONNECTION="${CLAIMFLOW_DESIGN_CONNECTION:-${ConnectionStrings__ClaimFlow:-}}"

if [[ -z "${CLAIMFLOW_DESIGN_CONNECTION}" ]]; then
  echo "Set CLAIMFLOW_DESIGN_CONNECTION or ConnectionStrings__ClaimFlow first." >&2
  echo "Example:" >&2
  echo "  export CLAIMFLOW_DESIGN_CONNECTION='Server=localhost,1433;Database=ClaimFlow;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True;Encrypt=False'" >&2
  exit 1
fi

case "${1:-}" in
  add)
    dotnet ef migrations add "${2:-InitialCreate}" \
      --project src/ClaimFlow.Data \
      --startup-project src/ClaimFlow.Data \
      --output-dir Migrations
    ;;
  update)
    dotnet ef database update \
      --project src/ClaimFlow.Data \
      --startup-project src/ClaimFlow.Data
    ;;
  remove)
    dotnet ef migrations remove \
      --project src/ClaimFlow.Data \
      --startup-project src/ClaimFlow.Data
    ;;
  script)
    dotnet ef migrations script \
      --project src/ClaimFlow.Data \
      --startup-project src/ClaimFlow.Data \
      --idempotent \
      --output "${2:-artifacts/claimflow-schema.sql}"
    ;;
  *)
    echo "Usage: $0 {add [name]|update|remove|script [output.sql]}" >&2
    exit 1
    ;;
esac
