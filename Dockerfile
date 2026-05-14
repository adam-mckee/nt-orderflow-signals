# ── Build stage ───────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/OrderFlowEngine/OrderFlowEngine.csproj ./
RUN dotnet restore

COPY src/OrderFlowEngine/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

# tzdata is needed for TimeZoneInfo ("America/New_York")
RUN apt-get update && apt-get install -y --no-install-recommends tzdata \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Mount a secrets volume at /run/secrets or inject env vars at runtime.
# Example env override: OrderFlow__Tradovate__Password=secret
ENTRYPOINT ["dotnet", "OrderFlowEngine.dll"]
