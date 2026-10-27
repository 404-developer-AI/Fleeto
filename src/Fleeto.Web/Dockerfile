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
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0@sha256:8af87eba5e7cdafb1514d7388be287365da97c132509e84dfe252ffd552398ca
ARG GOLANG_IMAGE=golang:1.27.3@sha256:f20d83a83e18dbd2a4714b8ffd4b30b287b6243962dd75608e86abcc63ba4efc

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
# The agent and the watchdog (0.2.1) for every supported platform: Windows and Linux, amd64 and arm64. Reproducible (-trimpath, no VCS
# stamp, pinned toolchain): the release workflow builds this stage on its own to list the SHA-256 of each binary in the signed manifest,
# then checks the binaries in the web and gateway images against it.
RUN pkg=github.com/404-developer-AI/Fleeto/agent/internal/version \
    && for platform in windows-amd64 windows-arm64 linux-amd64 linux-arm64; do \
        goos="${platform%-*}"; goarch="${platform#*-}"; suffix=""; \
        if [ "$goos" = windows ]; then suffix=".exe"; fi; \
        mkdir -p "/out/${platform}"; \
        for component in agent watchdog; do \
            GOOS="$goos" GOARCH="$goarch" go build -trimpath -buildvcs=false \
                -ldflags "-s -w -buildid= -X ${pkg}.Version=${FLEETO_VERSION} -X '${pkg}.ReleasePublicKeys=${FLEETO_RELEASE_PUBLIC_KEYS}'" \
                -o "/out/${platform}/fleeto-${component}${suffix}" ./cmd/fleeto-${component}; \
        done; \
    done \
    && (cd /out && sha256sum */fleeto-agent */fleeto-agent.exe */fleeto-watchdog */fleeto-watchdog.exe > SHA256SUMS)

# Only the agent binaries, for the release workflow: docker buildx build --target agent-binaries --output type=local,dest=<dir>.
# (Exporting the agent stage itself would write its whole filesystem.) The runtime image does not use this stage.
FROM scratch AS agent-binaries
COPY --from=agent /out/ /

# Fleeto Connect (0.8.0, MD-Files/PROXMOX-WEB.md): the program on the technician's PC that opens the Proxmox web interface, for Windows x64
# (one per-user exe without a console) and Ubuntu desktop x64 (a .deb), served by web to signed-in users. Reproducible like the agent; the
# release workflow lists both files with their SHA-256 in the signed manifest (connectFiles) and checks the web image against them.
FROM agent AS connect
ARG FLEETO_VERSION=0.0.0-dev
RUN pkg=github.com/404-developer-AI/Fleeto/agent/internal/version \
    && mkdir -p /out/windows-amd64 /out/linux-amd64 /tmp/connect \
    && GOOS=windows GOARCH=amd64 go build -trimpath -buildvcs=false -ldflags "-s -w -buildid= -H windowsgui -X ${pkg}.Version=${FLEETO_VERSION}" \
        -o /out/windows-amd64/fleeto-connect.exe ./cmd/fleeto-connect \
    && GOOS=linux GOARCH=amd64 go build -trimpath -buildvcs=false -ldflags "-s -w -buildid= -X ${pkg}.Version=${FLEETO_VERSION}" \
        -o /tmp/connect/fleeto-connect ./cmd/fleeto-connect \
    && packaging/fleeto-connect/build-deb.sh /tmp/connect/fleeto-connect "${FLEETO_VERSION}" /out/linux-amd64/fleeto-connect_amd64.deb \
    && rm -rf /out/windows-arm64 /out/linux-arm64 /out/*/fleeto-agent* /out/*/fleeto-watchdog* /out/SHA256SUMS \
    && (cd /out && sha256sum windows-amd64/fleeto-connect.exe linux-amd64/fleeto-connect_amd64.deb > SHA256SUMS)

# Only the files of Fleeto Connect, for the release workflow: docker buildx build --target connect-files --output type=local,dest=<dir>.
FROM scratch AS connect-files
COPY --from=connect /out/ /

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
COPY --from=connect --chown=root:root /out/ /app/connect/

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS=8080 \
    Fleeto__Database__Host=postgres \
    Fleeto__Database__Name=fleeto \
    Agent__BinariesDirectory=/app/agent \
    Connect__FilesDirectory=/app/connect

USER 10001:10001
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["curl", "--fail", "--silent", "--show-error", "--max-time", "4", "--output", "/dev/null", "http://127.0.0.1:8080/health"]
ENTRYPOINT ["dotnet", "/app/Fleeto.Web.dll"]
