# syntax=docker/dockerfile:1
#
# fleetify-tool: one-shot migrator (`migrate --fqdn ...`) run by install.sh through Compose, as the fleetify_migrator
# role. The Steaan signing commands are in the same binary but are never used on a VPS: no private Steaan key ever
# reaches an instance. Build context: repository root.
#
# Base images are pinned by digest; bump tag and digest together.
ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0@sha256:2fa828c68761b1b8c23d7662dc134421b9d3b59fe1425fdbc80804e390cdb24d
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0@sha256:8a153b5889d796b6450295b383596b13308c24c230515f8a7770ce1b94e0c460

FROM ${DOTNET_SDK_IMAGE} AS build
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Fleetify.Core/Fleetify.Core.csproj src/Fleetify.Core/
COPY src/Fleetify.Protocol/Fleetify.Protocol.csproj src/Fleetify.Protocol/
COPY src/Fleetify.Infrastructure/Fleetify.Infrastructure.csproj src/Fleetify.Infrastructure/
COPY src/Fleetify.Tools/Fleetify.Tools.csproj src/Fleetify.Tools/
RUN dotnet restore src/Fleetify.Tools/Fleetify.Tools.csproj

COPY src/Fleetify.Core/ src/Fleetify.Core/
COPY src/Fleetify.Protocol/ src/Fleetify.Protocol/
COPY src/Fleetify.Infrastructure/ src/Fleetify.Infrastructure/
COPY src/Fleetify.Tools/ src/Fleetify.Tools/

# Semicolon-separated base64 ed25519 keys; ';' is escaped as %3B for the MSBuild command line.
ARG FLEETIFY_LICENSE_PUBLIC_KEYS=""
ARG FLEETIFY_RELEASE_PUBLIC_KEYS=""
RUN dotnet publish src/Fleetify.Tools/Fleetify.Tools.csproj -c Release --no-restore -o /app \
        -p:UseAppHost=false \
        "-p:FleetifyLicensePublicKeys=$(printf '%s' "$FLEETIFY_LICENSE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
        "-p:FleetifyReleasePublicKeys=$(printf '%s' "$FLEETIFY_RELEASE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
    && test -f /app/fleetify-tool.dll

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
RUN groupadd --system --gid 10001 fleetify \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin fleetify
WORKDIR /app
COPY --from=build --chown=root:root /app/ /app/

# appsettings.json of the tool holds local development values; the container overrides them here.
ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Fleetify__Database__Host=postgres \
    Fleetify__Database__Port=5432 \
    Fleetify__Database__Name=fleetify

USER 10001:10001
# One-shot process: Compose waits for it with condition service_completed_successfully.
HEALTHCHECK NONE
ENTRYPOINT ["dotnet", "/app/fleetify-tool.dll"]
CMD ["--help"]
