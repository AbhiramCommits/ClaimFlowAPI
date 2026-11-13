# syntax=docker/dockerfile:1
# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Build.props global.json ./
COPY src/ClaimFlow.Shared/ClaimFlow.Shared.csproj src/ClaimFlow.Shared/
COPY src/ClaimFlow.Data/ClaimFlow.Data.csproj src/ClaimFlow.Data/
COPY src/ClaimFlow.Messaging/ClaimFlow.Messaging.csproj src/ClaimFlow.Messaging/
COPY src/ClaimFlow.Security/ClaimFlow.Security.csproj src/ClaimFlow.Security/
COPY src/ClaimFlow.Intake/ClaimFlow.Intake.csproj src/ClaimFlow.Intake/
RUN dotnet restore src/ClaimFlow.Intake/ClaimFlow.Intake.csproj

COPY src/ src/
RUN dotnet publish src/ClaimFlow.Intake/ClaimFlow.Intake.csproj \
    -c Release -o /app --no-restore /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && addgroup --system --gid 1001 claimflow \
    && adduser --system --uid 1001 --ingroup claimflow claimflow
COPY --from=build /app .
USER claimflow
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=20s --retries=5 \
    CMD curl -fsS http://127.0.0.1:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "ClaimFlow.Intake.dll"]
