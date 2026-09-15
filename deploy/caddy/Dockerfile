# syntax=docker/dockerfile:1
#
# fleetify-caddy: the host-level reverse proxy, one per VPS. Caddy with the layer4 module (caddy-l4), so connections
# for agents.<fqdn> are routed by TLS SNI to the instance gateway without being decrypted. Build context: this
# directory (no files are copied in). Released as ghcr.io/404-developer-ai/fleetify-caddy and pulled by digest.
#
# Versions are pinned: Caddy by tag, caddy-l4 by tag (module content is verified against the Go checksum database),
# base images by digest. Bump them together and re-run the Caddyfile adapt test in CI.
ARG CADDY_BUILDER_IMAGE=caddy:2.11.4-builder-alpine@sha256:1a1689db91cfb390b2d856a1b3774e796852822cd723fa54c475b272f82bb4b7
ARG CADDY_RUNTIME_IMAGE=caddy:2.11.4-alpine@sha256:5f5c8640aae01df9654968d946d8f1a56c497f1dd5c5cda4cf95ab7c14d58648

FROM ${CADDY_BUILDER_IMAGE} AS build
SHELL ["/bin/ash", "-o", "pipefail", "-c"]
ARG CADDY_VERSION=v2.11.4
# caddy-l4 v0.1.2 = commit 42db5690dea199f930a6f08005fe2e4aab10dcc9
ARG CADDY_L4_VERSION=v0.1.2
ENV GOTOOLCHAIN=local GOFLAGS=-trimpath
RUN xcaddy build "${CADDY_VERSION}" \
        --with "github.com/mholt/caddy-l4@${CADDY_L4_VERSION}" \
        --output /out/caddy \
    && /out/caddy list-modules > /tmp/caddy-modules \
    && grep -qx 'layer4' /tmp/caddy-modules \
    && /out/caddy version

FROM ${CADDY_RUNTIME_IMAGE} AS runtime
COPY --from=build /out/caddy /usr/bin/caddy
# Certificates and the ACME account live in /data (a volume); the generated Caddyfile is mounted read-only in
# /etc/caddy. The admin API listens on a unix socket in /run/caddy only, never on TCP.
ENV XDG_DATA_HOME=/data XDG_CONFIG_HOME=/config
# The generated Caddyfile always contains a loopback-only health site on 127.0.0.1:2020.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["wget", "-q", "-O", "/dev/null", "http://127.0.0.1:2020/"]
ENTRYPOINT ["caddy"]
CMD ["run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
