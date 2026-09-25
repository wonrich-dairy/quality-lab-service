# ── Build ────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the project file only, so this layer is cached until dependencies change
COPY src/QualityLab.Api/QualityLab.Api.csproj src/QualityLab.Api/
RUN dotnet restore src/QualityLab.Api/QualityLab.Api.csproj

COPY src/ src/
RUN dotnet publish src/QualityLab.Api/QualityLab.Api.csproj -c Release -o /app --no-restore

# ── Runtime ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG GIT_SHA=local
ENV GIT_SHA=$GIT_SHA
WORKDIR /app

# curl is only used by the container HEALTHCHECK
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

ARG GIT_SHA=local
ENV GIT_SHA=$GIT_SHA \
    ASPNETCORE_URLS=http://+:8080

COPY --from=build /app .

# Non-root user built into the .NET images
USER $APP_UID

EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fs http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "QualityLab.Api.dll"]