# syntax=docker/dockerfile:1
#
# fleeto-tool: one-shot migrator (`migrate --fqdn ...`) run by install.sh through Compose, as the fleeto_migrator
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
COPY src/Fleeto.Core/Fleeto.Core.csproj src/Fleeto.Core/
COPY src/Fleeto.Protocol/Fleeto.Protocol.csproj src/Fleeto.Protocol/
COPY src/Fleeto.Infrastructure/Fleeto.Infrastructure.csproj src/Fleeto.Infrastructure/
COPY src/Fleeto.Tools/Fleeto.Tools.csproj src/Fleeto.Tools/
RUN dotnet restore src/Fleeto.Tools/Fleeto.Tools.csproj

COPY src/Fleeto.Core/ src/Fleeto.Core/
COPY src/Fleeto.Protocol/ src/Fleeto.Protocol/
COPY src/Fleeto.Infrastructure/ src/Fleeto.Infrastructure/
COPY src/Fleeto.Tools/ src/Fleeto.Tools/

# Semicolon-separated base64 ed25519 keys; ';' is escaped as %3B for the MSBuild command line.
ARG FLEETO_LICENSE_PUBLIC_KEYS=""
ARG FLEETO_RELEASE_PUBLIC_KEYS=""
RUN dotnet publish src/Fleeto.Tools/Fleeto.Tools.csproj -c Release --no-restore -o /app \
        -p:UseAppHost=false \
        "-p:FleetoLicensePublicKeys=$(printf '%s' "$FLEETO_LICENSE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
        "-p:FleetoReleasePublicKeys=$(printf '%s' "$FLEETO_RELEASE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
    && test -f /app/fleeto-tool.dll

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
RUN groupadd --system --gid 10001 fleeto \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin fleeto
WORKDIR /app
COPY --from=build --chown=root:root /app/ /app/

# appsettings.json of the tool holds local development values; the container overrides them here.
ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Fleeto__Database__Host=postgres \
    Fleeto__Database__Port=5432 \
    Fleeto__Database__Name=fleeto

USER 10001:10001
# One-shot process: Compose waits for it with condition service_completed_successfully.
HEALTHCHECK NONE
ENTRYPOINT ["dotnet", "/app/fleeto-tool.dll"]
CMD ["--help"]
