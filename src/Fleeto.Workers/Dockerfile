# syntax=docker/dockerfile:1
#
# fleeto-workers: background jobs (checks, alerting, outbound email, retention, backups). Includes the PostgreSQL
# 17 client (pg_dump) for the nightly backup. Build context: repository root.
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
COPY src/Fleeto.Workers/Fleeto.Workers.csproj src/Fleeto.Workers/
RUN dotnet restore src/Fleeto.Workers/Fleeto.Workers.csproj

COPY src/Fleeto.Core/ src/Fleeto.Core/
COPY src/Fleeto.Protocol/ src/Fleeto.Protocol/
COPY src/Fleeto.Infrastructure/ src/Fleeto.Infrastructure/
COPY src/Fleeto.Workers/ src/Fleeto.Workers/

# Semicolon-separated base64 ed25519 keys; ';' is escaped as %3B for the MSBuild command line.
ARG FLEETO_LICENSE_PUBLIC_KEYS=""
ARG FLEETO_RELEASE_PUBLIC_KEYS=""
RUN dotnet publish src/Fleeto.Workers/Fleeto.Workers.csproj -c Release --no-restore -o /app \
        -p:UseAppHost=false \
        "-p:FleetoLicensePublicKeys=$(printf '%s' "$FLEETO_LICENSE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
        "-p:FleetoReleasePublicKeys=$(printf '%s' "$FLEETO_RELEASE_PUBLIC_KEYS" | sed 's/;/%3B/g')" \
    && rm -f /app/appsettings.Development.json \
    && test -f /app/fleeto-workers.dll

# ---------------------------------------------------------------------------------------------------------------
# PGDG signing key, verified by full fingerprint before it is trusted. gnupg stays in this throwaway stage.
FROM ${DOTNET_RUNTIME_IMAGE} AS pgdg-key
SHELL ["/bin/bash", "-o", "pipefail", "-c"]
ARG PGDG_KEY_FINGERPRINT=B97B0AFCAA1A47F044F244A07FCC7D46ACCC4CF8
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl gnupg \
    && curl --fail --silent --show-error --location --proto '=https' --tlsv1.2 \
        https://www.postgresql.org/media/keys/ACCC4CF8.asc --output /tmp/pgdg.asc \
    && GNUPGHOME="$(mktemp -d)" \
    && export GNUPGHOME \
    && actual="$(gpg --batch --show-keys --with-colons /tmp/pgdg.asc | awk -F: '$1 == "fpr" { print $10; exit }')" \
    && if [ "$actual" != "$PGDG_KEY_FINGERPRINT" ]; then \
         echo "PGDG key fingerprint mismatch: expected $PGDG_KEY_FINGERPRINT, got $actual" >&2; exit 1; \
       fi \
    && gpg --batch --dearmor --output /tmp/pgdg.gpg /tmp/pgdg.asc

FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
ARG POSTGRESQL_CLIENT_PACKAGE=postgresql-client-17
COPY --from=pgdg-key /tmp/pgdg.gpg /usr/share/keyrings/pgdg.gpg
# signed-by restricts the PGDG key to the PGDG repository only.
RUN . /etc/os-release \
    && echo "deb [signed-by=/usr/share/keyrings/pgdg.gpg] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends "$POSTGRESQL_CLIENT_PACKAGE" \
    && rm -rf /var/lib/apt/lists/* \
    && pg_dump --version \
    && groupadd --system --gid 10001 fleeto \
    && useradd --system --uid 10001 --gid 10001 --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin fleeto \
    && install -d -o 10001 -g 10001 -m 0770 /var/lib/fleeto/work
WORKDIR /app
COPY --from=build --chown=root:root /app/ /app/

ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Fleeto__Database__Host=postgres \
    Fleeto__Database__Name=fleeto

USER 10001:10001
# No port: the job loop touches a heartbeat file in /tmp and `healthcheck` checks its age.
HEALTHCHECK --interval=30s --timeout=10s --start-period=90s --retries=3 CMD ["dotnet", "/app/fleeto-workers.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/fleeto-workers.dll"]
