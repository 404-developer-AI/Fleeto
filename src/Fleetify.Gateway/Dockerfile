# syntax=docker/dockerfile:1
#
# fleetify-gateway: agent endpoint (HTTPS 8443, TLS passed through untouched by the host Caddy) and health endpoint
# (HTTP 8081, container-internal). It also carries the agent and watchdog binaries it serves for agent updates (/app/agent, 0.2.1), built
# exactly as in the web image. Build context: repository root.
#
# Base images are pinned by digest; bump tag and digest together.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c
ARG GOLANG_IMAGE=golang:1.27.1@sha256:f44f6e88636cfb311f9ebace870ded69d943f227bb3cb27d32ffd84ea18c43ea

# ---------------------------------------------------------------------------------------------------------------
# Agent binaries: must stay identical to the agent stage of src/Fleetify.Web/Dockerfile (the release workflow compares both images).
FROM ${GOLANG_IMAGE} AS agent
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
WORKDIR /src/agent
ENV CGO_ENABLED=0 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
COPY agent/go.mod agent/go.sum ./
RUN go mod download && go mod verify
COPY agent/ ./
ARG FLEETIFY_VERSION=0.0.0-dev
ARG FLEETIFY_RELEASE_PUBLIC_KEYS=""
# The agent and the watchdog (0.2.1). Reproducible (-trimpath, no VCS stamp, pinned toolchain): the release workflow builds this stage on
# its own to list the SHA-256 of each binary in the signed manifest, then checks the binaries in the web and gateway images against it.
RUN pkg=github.com/404-developer-AI/Fleeto/agent/internal/version \
    && mkdir -p /out/windows-amd64 \
    && for component in agent watchdog; do \
        GOOS=windows GOARCH=amd64 go build -trimpath -buildvcs=false \
            -ldflags "-s -w -buildid= -X ${pkg}.Version=${FLEETIFY_VERSION} -X '${pkg}.ReleasePublicKeys=${FLEETIFY_RELEASE_PUBLIC_KEYS}'" \
            -o /out/windows-amd64/fleetify-${component}.exe ./cmd/fleetify-${component}; \
    done \
    && (cd /out && sha256sum windows-amd64/fleetify-agent.exe windows-amd64/fleetify-watchdog.exe > SHA256SUMS)

# ---------------------------------------------------------------------------------------------------------------

FROM ${DOTNET_SDK_IMAGE} AS build
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Fleetify.Core/Fleetify.Core.csproj src/Fleetify.Core/
COPY src/Fleetify.Protocol/Fleetify.Protocol.csproj src/Fleetify.Protocol/
COPY src/Fleetify.Infrastructure/Fleetify.Infrastructure.csproj src/Fleetify.Infrastructure/
COPY src/Fleetify.Gateway/Fleetify.Gateway.csproj src/Fleetify.Gateway/
RUN dotnet restore src/Fleetify.Gateway/Fleetify.Gateway.csproj

COPY src/Fleetify.Core/ src/Fleetify.Core/
COPY src/Fleetify.Protocol/ src/Fleetify.Protocol/
COPY src/Fleetify.Infrastructure/ src/Fleetify.Infrastructure/
COPY src/Fleetify.Gateway/ src/Fleetify.Gateway/

# Semicolon-separated base64 ed25519 keys; ';' is escaped as %3B for the MSBuild command line.
ARG FLEETIFY_LICENSE_PUBLIC_KEYS=""
ARG FLEETIFY_RELEASE_PUBLIC_KEYS=""
RUN dotnet publish src/Fleetify.Gateway/Fleetify.Gateway.csproj -c Release --no-restore -o /app \
        -p:UseAppHost=false \
        "-p:FleetifyLicensePublicKeys=$(printf '%s' "$FLEETIFY_LICENSE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
        "-p:FleetifyReleasePublicKeys=$(printf '%s' "$FLEETIFY_RELEASE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
    && rm -f /app/appsettings.Development.json \
    && test -f /app/Fleetify.Gateway.dll

FROM ${DOTNET_ASPNET_IMAGE} AS runtime
# curl is the only extra package: the HTTP health check needs a client and the gateway has no healthcheck mode.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 fleetify \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin fleetify
WORKDIR /app
COPY --from=build --chown=root:root /app/ /app/
COPY --from=agent --chown=root:root /out/ /app/agent/

# Kestrel endpoints come from the Gateway configuration section, not from ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS.
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS= \
    Fleetify__Database__Host=postgres \
    Fleetify__Database__Name=fleetify \
    Gateway__ListenAddress=0.0.0.0 \
    Gateway__AgentPort=8443 \
    Gateway__HealthPort=8081 \
    Gateway__AgentBinariesDirectory=/app/agent \
    Gateway__ReleaseDirectory=/app/release

USER 10001:10001
EXPOSE 8443
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["curl", "--fail", "--silent", "--show-error", "--max-time", "4", "--output", "/dev/null", "http://127.0.0.1:8081/health"]
ENTRYPOINT ["dotnet", "/app/Fleetify.Gateway.dll"]
