# syntax=docker/dockerfile:1

# --- build: run the SDK on the build host arch, emit architecture-neutral IL (no QEMU emulation) ---
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the project files first so this layer caches across source-only edits.
COPY src/Racearr.Core/Racearr.Core.csproj src/Racearr.Core/
COPY src/Racearr.Web/Racearr.Web.csproj src/Racearr.Web/
RUN dotnet restore src/Racearr.Web/Racearr.Web.csproj

COPY src/ src/
RUN dotnet publish src/Racearr.Web/Racearr.Web.csproj -c Release --no-restore -o /app

# --- runtime: pulled per target architecture from the multi-arch manifest ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

LABEL org.opencontainers.image.title="racearr" \
      org.opencontainers.image.description="Aggressive download racer + pickup/speed SLAs for Radarr/Sonarr + qBittorrent" \
      org.opencontainers.image.source="https://github.com/dragoshont/racearr" \
      org.opencontainers.image.licenses="MIT"

# curl backs the container HEALTHCHECK; Kubernetes uses its own httpGet probe on /healthz.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Writable config/database directory owned by the non-root runtime user.
RUN mkdir -p /config && chown 1000:1000 /config
USER 1000

ENV DOTNET_EnableDiagnostics=0 \
    HEALTH_PORT=9797 \
    DB_PATH=/config/racearr.db
VOLUME ["/config"]
EXPOSE 9797

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=4 \
  CMD curl -fsS "http://localhost:${HEALTH_PORT}/healthz" || exit 1

ENTRYPOINT ["dotnet", "Racearr.Web.dll"]
