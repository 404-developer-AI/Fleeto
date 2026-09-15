# syntax=docker/dockerfile:1
#
# fleeto-web: Blazor Server UI (and later the public REST API) on HTTP 8080 behind the host Caddy. The image also
# carries the agent binaries the instance hands out to endpoints (/app/agent). Build context: repository root.
#
#   docker build -f src/Fleeto.Web/Dockerfile \
#     --build-arg FLEETO_VERSION=0.1.0 \
#     --build-arg FLEETO_LICENSE_PUBLIC_KEYS=... --build-arg FLEETO_RELEASE_PUBLIC_KEYS=... .
#
# Base images are pinned by digest; bump tag and digest together.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0@sha256:6a94333d37514e385650a3c81a55e5350b67253dbe136e9cf17e499c35606a8c
ARG GOLANG_IMAGE=golang:1.27.1@sha256:f44f6e88636cfb311f9ebace870ded69d943f227bb3cb27d32ffd84ea18c43ea

# ---------------------------------------------------------------------------------------------------------------
# Agent: one static binary per platform. 0.1.0 ships Windows amd64.
FROM ${GOLANG_IMAGE} AS agent
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
WORKDIR /src/agent
# GOTOOLCHAIN=local: never download a different Go toolchain during the build. GOFLAGS=-mod=readonly: go.sum is the
# only source of module hashes; modules are verified against it (and the checksum database) on download.
ENV CGO_ENABLED=0 GOTOOLCHAIN=local GOFLAGS=-mod=readonly
COPY agent/go.mod agent/go.sum ./
RUN go mod download && go mod verify
COPY agent/ ./
ARG FLEETO_VERSION=0.0.0-dev
ARG FLEETO_RELEASE_PUBLIC_KEYS=""
# The agent and the watchdog (0.2.1). Reproducible (-trimpath, no VCS stamp, pinned toolchain): the release workflow builds this stage on
# its own to list the SHA-256 of each binary in the signed manifest, then checks the binaries in the web and gateway images against it.
RUN pkg=github.com/404-developer-AI/Fleeto/agent/internal/version \
    && mkdir -p /out/windows-amd64 \
    && for component in agent watchdog; do \
        GOOS=windows GOARCH=amd64 go build -trimpath -buildvcs=false \
            -ldflags "-s -w -buildid= -X ${pkg}.Version=${FLEETO_VERSION} -X '${pkg}.ReleasePublicKeys=${FLEETO_RELEASE_PUBLIC_KEYS}'" \
            -o /out/windows-amd64/fleeto-${component}.exe ./cmd/fleeto-${component}; \
    done \
    && (cd /out && sha256sum windows-amd64/fleeto-agent.exe windows-amd64/fleeto-watchdog.exe > SHA256SUMS)

# ---------------------------------------------------------------------------------------------------------------
FROM ${DOTNET_SDK_IMAGE} AS build
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Fleeto.Core/Fleeto.Core.csproj src/Fleeto.Core/
COPY src/Fleeto.Protocol/Fleeto.Protocol.csproj src/Fleeto.Protocol/
COPY src/Fleeto.Infrastructure/Fleeto.Infrastructure.csproj src/Fleeto.Infrastructure/
COPY src/Fleeto.Web/Fleeto.Web.csproj src/Fleeto.Web/
RUN dotnet restore src/Fleeto.Web/Fleeto.Web.csproj

COPY src/Fleeto.Core/ src/Fleeto.Core/
COPY src/Fleeto.Protocol/ src/Fleeto.Protocol/
COPY src/Fleeto.Infrastructure/ src/Fleeto.Infrastructure/
COPY src/Fleeto.Web/ src/Fleeto.Web/

# Semicolon-separated base64 ed25519 keys; ';' is escaped as %3B for the MSBuild command line.
ARG FLEETO_LICENSE_PUBLIC_KEYS=""
ARG FLEETO_RELEASE_PUBLIC_KEYS=""
RUN dotnet publish src/Fleeto.Web/Fleeto.Web.csproj -c Release --no-restore -o /app \
        -p:UseAppHost=false \
        "-p:FleetoLicensePublicKeys=$(printf '%s' "$FLEETO_LICENSE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
        "-p:FleetoReleasePublicKeys=$(printf '%s' "$FLEETO_RELEASE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
    && rm -f /app/appsettings.Development.json \
    && test -f /app/Fleeto.Web.dll \
    && test -f /app/wwwroot/_framework/blazor.web.js \
    && grep -q '"Route":"_framework/blazor.web.js"' /app/Fleeto.Web.staticwebassets.endpoints.json

# ---------------------------------------------------------------------------------------------------------------
FROM ${DOTNET_ASPNET_IMAGE} AS runtime
# curl is the only extra package: the HTTP health check needs a client and web has no healthcheck mode.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --system --gid 10001 fleeto \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin fleeto \
    && install -d -o 10001 -g 10001 -m 0700 /app/logs /app/keys
WORKDIR /app
COPY --from=build --chown=root:root /app/ /app/
COPY --from=agent --chown=root:root /out/ /app/agent/

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS=8080 \
    Fleeto__Database__Host=postgres \
    Fleeto__Database__Name=fleeto \
    Agent__BinariesDirectory=/app/agent

USER 10001:10001
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["curl", "--fail", "--silent", "--show-error", "--max-time", "4", "--output", "/dev/null", "http://127.0.0.1:8080/health"]
ENTRYPOINT ["dotnet", "/app/Fleeto.Web.dll"]
