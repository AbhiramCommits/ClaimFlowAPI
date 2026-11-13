# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY src/ClaimFlow.Shared/ClaimFlow.Shared.csproj src/ClaimFlow.Shared/
COPY src/ClaimFlow.Data/ClaimFlow.Data.csproj src/ClaimFlow.Data/
COPY tools/ClaimFlow.Migrator/ClaimFlow.Migrator.csproj tools/ClaimFlow.Migrator/
RUN dotnet restore tools/ClaimFlow.Migrator/ClaimFlow.Migrator.csproj

COPY src/ src/
COPY tools/ClaimFlow.Migrator/ tools/ClaimFlow.Migrator/
RUN dotnet publish tools/ClaimFlow.Migrator/ClaimFlow.Migrator.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
RUN addgroup --system --gid 1001 claimflow && adduser --system --uid 1001 --ingroup claimflow claimflow
COPY --from=build /app .
COPY db/ /app/db/
USER claimflow
ENV DB_SCRIPTS_PATH=/app/db
ENTRYPOINT ["dotnet", "ClaimFlow.Migrator.dll"]
