# syntax=docker/dockerfile:1
#
# fleetify-signer: the only container that mounts signer.key. No listening port, no outbound network (Compose puts it
# on the internal network only). Build context: repository root.
#
#   docker build -f src/Fleetify.Signer/Dockerfile \
#     --build-arg FLEETIFY_LICENSE_PUBLIC_KEYS=... --build-arg FLEETIFY_RELEASE_PUBLIC_KEYS=... .
#
# Base images are pinned by digest; bump tag and digest together.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0@sha256:8a153b5889d796b6450295b383596b13308c24c230515f8a7770ce1b94e0c460

FROM ${DOTNET_SDK_IMAGE} AS build
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

# Restore first with only project and props files, so the package layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Fleetify.Core/Fleetify.Core.csproj src/Fleetify.Core/
COPY src/Fleetify.Protocol/Fleetify.Protocol.csproj src/Fleetify.Protocol/
COPY src/Fleetify.Infrastructure/Fleetify.Infrastructure.csproj src/Fleetify.Infrastructure/
COPY src/Fleetify.Signer/Fleetify.Signer.csproj src/Fleetify.Signer/
RUN dotnet restore src/Fleetify.Signer/Fleetify.Signer.csproj

COPY src/Fleetify.Core/ src/Fleetify.Core/
COPY src/Fleetify.Protocol/ src/Fleetify.Protocol/
COPY src/Fleetify.Infrastructure/ src/Fleetify.Infrastructure/
COPY src/Fleetify.Signer/ src/Fleetify.Signer/

# Public verification keys: semicolon-separated base64 ed25519 keys. MSBuild treats ';' on the command line as a
# property separator, so it is escaped as %3B.
ARG FLEETIFY_LICENSE_PUBLIC_KEYS=""
ARG FLEETIFY_RELEASE_PUBLIC_KEYS=""
RUN dotnet publish src/Fleetify.Signer/Fleetify.Signer.csproj -c Release --no-restore -o /app \
        -p:UseAppHost=false \
        "-p:FleetifyLicensePublicKeys=$(printf '%s' "$FLEETIFY_LICENSE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
        "-p:FleetifyReleasePublicKeys=$(printf '%s' "$FLEETIFY_RELEASE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
    && rm -f /app/appsettings.Development.json \
    && test -f /app/fleetify-signer.dll

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
# Fixed non-root identity. install.sh makes the mounted secret files readable for group 10001 only.
RUN groupadd --system --gid 10001 fleetify \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin fleetify
WORKDIR /app
COPY --from=build --chown=root:root /app/ /app/

# Production defaults. DOTNET_EnableDiagnostics=0 removes the diagnostics IPC socket (no debugger or dump attach).
ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Fleetify__Database__Host=postgres \
    Fleetify__Database__Name=fleetify

USER 10001:10001
# No port: the signing loop touches a heartbeat file in /tmp and `healthcheck` checks its age.
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 CMD ["dotnet", "/app/fleetify-signer.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/fleetify-signer.dll"]
