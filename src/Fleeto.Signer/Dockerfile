# syntax=docker/dockerfile:1
#
# fleeto-signer: the only container that mounts signer.key. No listening port, no outbound network (Compose puts it
# on the internal network only). Build context: repository root.
#
#   docker build -f src/Fleeto.Signer/Dockerfile \
#     --build-arg FLEETO_LICENSE_PUBLIC_KEYS=... --build-arg FLEETO_RELEASE_PUBLIC_KEYS=... .
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
COPY src/Fleeto.Core/Fleeto.Core.csproj src/Fleeto.Core/
COPY src/Fleeto.Protocol/Fleeto.Protocol.csproj src/Fleeto.Protocol/
COPY src/Fleeto.Infrastructure/Fleeto.Infrastructure.csproj src/Fleeto.Infrastructure/
COPY src/Fleeto.Signer/Fleeto.Signer.csproj src/Fleeto.Signer/
RUN dotnet restore src/Fleeto.Signer/Fleeto.Signer.csproj

COPY src/Fleeto.Core/ src/Fleeto.Core/
COPY src/Fleeto.Protocol/ src/Fleeto.Protocol/
COPY src/Fleeto.Infrastructure/ src/Fleeto.Infrastructure/
COPY src/Fleeto.Signer/ src/Fleeto.Signer/

# Public verification keys: semicolon-separated base64 ed25519 keys. MSBuild treats ';' on the command line as a
# property separator, so it is escaped as %3B.
ARG FLEETO_LICENSE_PUBLIC_KEYS=""
ARG FLEETO_RELEASE_PUBLIC_KEYS=""
RUN dotnet publish src/Fleeto.Signer/Fleeto.Signer.csproj -c Release --no-restore -o /app \
        -p:UseAppHost=false \
        "-p:FleetoLicensePublicKeys=$(printf '%s' "$FLEETO_LICENSE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
        "-p:FleetoReleasePublicKeys=$(printf '%s' "$FLEETO_RELEASE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
    && rm -f /app/appsettings.Development.json \
    && test -f /app/fleeto-signer.dll

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
# Fixed non-root identity. install.sh makes the mounted secret files readable for group 10001 only.
RUN groupadd --system --gid 10001 fleeto \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin fleeto
WORKDIR /app
COPY --from=build --chown=root:root /app/ /app/

# Production defaults. DOTNET_EnableDiagnostics=0 removes the diagnostics IPC socket (no debugger or dump attach).
ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Fleeto__Database__Host=postgres \
    Fleeto__Database__Name=fleeto

USER 10001:10001
# No port: the signing loop touches a heartbeat file in /tmp and `healthcheck` checks its age.
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 CMD ["dotnet", "/app/fleeto-signer.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/fleeto-signer.dll"]
