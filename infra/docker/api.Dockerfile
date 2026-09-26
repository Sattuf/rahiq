# syntax=docker/dockerfile:1.7
# Rahiq API. Build context = repository root: the build embeds rahiq-plan/seed/taxonomy.json and backend/db/migrations.
#   docker build -f infra/docker/api.Dockerfile -t rahiq-api:$(git rev-parse --short HEAD) .
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY rahiq-plan/seed/taxonomy.json rahiq-plan/seed/taxonomy.json
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY .editorconfig .editorconfig
# Restore on project files alone so the package layer is cached across code changes.
COPY backend/src/ /tmp/src/
RUN cd /tmp/src && find . -name '*.csproj' -exec install -D {} /src/backend/src/{} \; && rm -rf /tmp/src
RUN dotnet restore backend/src/Rahiq.Api/Rahiq.Api.csproj
COPY backend/src backend/src
COPY backend/db backend/db
RUN dotnet publish backend/src/Rahiq.Api/Rahiq.Api.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
# SkiaSharp (label/QR rendering) needs fontconfig; curl is for the container health check.
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
RUN mkdir -p /app/App_Data/keys && chown -R app:app /app/App_Data
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=1 \
    DataProtection__KeysPath=/app/App_Data/keys
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=5 CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "Rahiq.Api.dll"]
