# Fleeto deployment

Everything needed to run Fleeto on an Ubuntu VPS: the per-instance Compose stack, the host proxy, and `install.sh`,
which installs and updates instances. Design background: `MD-Files/ARCHITECTURE.md` sections 1, 5 and 7.

| Path | Purpose |
|---|---|
| `install.sh` | Installs and updates instances. The release build embeds the files below and the Steaan release public keys. |
| `compose/compose.yml` | Template of one instance stack (postgres, migrator, signer, gateway, workers, web). |
| `postgres/init/10-fleetify-roles.sh` | Creates the database roles at first start of an instance database. |
| `postgres/archive-wal.sh` | PostgreSQL `archive_command`: copies WAL segments into the spool the workers upload from. |
| `caddy/Dockerfile`, `caddy/compose.yml` | Host proxy image (Caddy + layer4 module) and its Compose file. |
| `ci/bundle-install.sh` | Produces the release `install.sh` (version, public keys, embedded templates). |
| `ci/test-deploy.sh` | Self-test of the bundle, signature checks, Caddyfile generation and Compose rendering. |
| `ci/branding-check.sh` | Fails on "Fleetify", "device" or "machine" in user-visible UI text. |
| `release-rollback` | Rollback mode (`images` or `restore`) of the next release. |
| `RELEASING.md` | How a release is built, signed offline and published. |

## VPS requirements

- Ubuntu 24.04 or 22.04, amd64, root access, a public IPv4 address (IPv6 optional).
- Inbound TCP 80 and 443 open (80 for certificate issuance and redirects; 443 for the web UI, the API and agents).
  Nothing else needs to be reachable from outside: every instance port is bound to 127.0.0.1.
- Outbound HTTPS to `get.fleeto.app`, `ghcr.io`, Docker Hub, `download.docker.com`, the Ubuntu mirrors, Let's
  Encrypt, and the backup storage and SMTP server of each instance.
- If `/etc/docker/daemon.json` already exists, install.sh leaves it alone. Add a `default-address-pools` entry
  yourself when you plan more than about 10 instances (each instance uses three Docker networks), and add that range
  to `ReverseProxy__KnownNetworks` in the web service if it is not 172.16.0.0/12, 10.210.0.0/16 or 192.168.0.0/16.

### Footprint per instance (estimate until the 10,000-agent load test has run)

| Size | Endpoints | RAM | vCPU | Disk |
|---|---|---|---|---|
| Small | up to 250 | 2 GB | 1 | 20 GB |
| Medium | up to 2,000 | 4 GB | 2 | 60 GB |
| Large | up to 10,000 | 8 to 12 GB | 4 | 200 GB+ |

Rough split for a small instance: postgres 600 MB to 1 GB, web 300 to 500 MB, gateway 150 MB plus about 30 KB per
connected agent, workers 200 MB, signer 100 MB. Disk grows with check results, logs and retention settings. Add 100 MB
of RAM for the shared host proxy. PostgreSQL memory settings follow `POSTGRES_MEMORY` and `POSTGRES_CPUS` in
`instance.conf` (default 1GB and 2, applied when the database is created). Treat these figures as a starting point:
they are not measured yet.

## DNS records

For an instance on `rmm.customer.example`, both names must point to the VPS before installing:

```
rmm.customer.example          A     <public IPv4 of the VPS>
agents.rmm.customer.example   A     <public IPv4 of the VPS>
(and matching AAAA records if the VPS has IPv6; remove AAAA records that point elsewhere)
```

install.sh checks both names and prints the exact records to create when they are missing or wrong.

## First install

```
curl -fsSLO https://get.fleeto.app/install.sh
curl -fsSLO https://get.fleeto.app/install.sh.sig
openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in install.sh -sigfile install.sh.sig
sudo bash install.sh --fqdn rmm.customer.example
```

`steaan-release.pub` comes from the Fleeto website or the customer documentation, never from the same place as the
script. The first run on a VPS installs the required packages, Docker Engine (from Docker's apt repository, key
fingerprint checked) and the host proxy. For the instance it then:

1. checks DNS, verifies the release manifest and pulls every image by digest;
2. creates `/opt/fleetify/<instance>/` and generates `root.key`, `signer.key` and one database password per role;
3. starts PostgreSQL, runs the migrator, starts the stack and waits until every service is healthy;
4. adds the HTTPS route and the SNI passthrough route to the host proxy;
5. prints the URL, the one-time first-admin setup link (valid 24 hours) and the key ceremony reminder.

Running it again is safe: an interrupted install continues, existing secrets are never overwritten.

### Layout on the VPS

```
/opt/fleetify/                         0700 root
  bin/install.sh                       the verified install.sh of the latest applied release
  caddy/                               host proxy: compose.yml, caddy.conf (image digest), config/Caddyfile (generated)
  <instance>/                          0700 root; <instance> is the FQDN with dots replaced by dashes
    compose.yml, instance.conf         no secrets (instance.conf: FQDN, version, state, loopback ports, image digests)
    secrets/                           0700 root; files 0440 root:10001
      root.key, signer.key             32 random bytes, base64
      db-<role>.password               postgres, migrator, web, gateway, signer, workers, backup
    postgres/                          init script and archive_command
    wal-spool/                         2770 70:10001, WAL segments waiting for upload by the workers
    work/                              0700 10001, scratch space of the nightly backup
    backups/                           0700 root, pre-update database dumps (the last 3)
    state/                             history.log, configuration snapshot of the last update
```

Why the secret files are `0440 root:10001` and not `0600 root`: Compose mounts file secrets as bind mounts that keep
the host owner and mode, and the containers run as uid 10001 (postgres joins group 10001). The `secrets/` directory
itself is `0700 root`, so no other account on the VPS can reach the files; inside the stack each file is mounted only
into the containers listed in `compose/compose.yml`.

## Update

```
sudo /opt/fleetify/bin/install.sh --fqdn rmm.customer.example              # to the latest release
sudo /opt/fleetify/bin/install.sh --fqdn rmm.customer.example --version 0.1.1
sudo /opt/fleetify/bin/install.sh --all                                    # every instance on the VPS
sudo /opt/fleetify/bin/install.sh --check                                  # installed versus latest, no changes
sudo /opt/fleetify/bin/install.sh --list
```

An update verifies the new manifest (and switches to the install.sh listed in it), pulls the images, dumps the
database to `backups/`, runs the migrator, restarts the stack and runs the health check (container health, web
`/health`, a TLS handshake with the gateway). When anything fails it rolls back to the previous images; for a release
marked `rollback: restore` it first restores the pre-update dump, and it asks for confirmation (or `--yes`) before
such an update starts. install.sh never downgrades an instance.

## Backups

- Nightly `pg_dump` and continuous WAL archiving are done by fleetify-workers, encrypted with the instance's backup
  public key before upload to S3-compatible storage in the EU. Configure the destination and the backup public key in
  Settings, Backups. Until then the dashboard warns and WAL segments accumulate in `wal-spool/`.
- The pre-update dumps in `backups/` are a rollback aid only. They are not encrypted and never leave the VPS.
- Instance secrets (`secrets/`) are never part of a backup.

## Key ceremony

Do this on the day an instance is installed. The full procedure is part of the key ceremony document planned in
`MD-Files/ARCHITECTURE.md` section 5; the minimum:

1. Copy `secrets/root.key` and `secrets/signer.key` to two offline locations (for example two encrypted USB drives
   kept in different places). Record who holds them.
2. On an offline machine, create the backup key pair: `fleetify-tool backup keygen --out <folder>`. Keep
   `backup.key` offline next to the instance keys; paste the public key into Settings, Backups.
3. Store read credentials for the backup destination with the offline keys.
4. Rehearse a restore (below) onto a scratch VPS before relying on the backups.

## Restore (outline)

A restore needs the offline backup private key, the original `root.key` and `signer.key`, and read access to the
backup destination.

1. Prepare a VPS with DNS for the same FQDN and run `install.sh --fqdn <fqdn>` once so the stack and secrets exist.
2. Stop the application services: `docker compose -p fleetify-<instance> --env-file instance.conf -f compose.yml stop web gateway workers signer migrator`.
3. Replace `secrets/root.key` and `secrets/signer.key` with the offline originals (mode 0440, root:10001).
4. Download the latest nightly dump and decrypt it offline: `fleetify-tool backup decrypt --key backup.key --in <file> --out fleetify.dump`.
5. Recreate the database and restore, as the postgres superuser inside the postgres container:
   `DROP DATABASE fleetify WITH (FORCE); CREATE DATABASE fleetify OWNER fleetify_migrator;`, grant CONNECT to the
   fleetify roles, `CREATE EXTENSION timescaledb; SELECT timescaledb_pre_restore();`, then
   `pg_restore --dbname=fleetify fleetify.dump` and `SELECT timescaledb_post_restore();`
   (`restore_database` in install.sh does exactly this for pre-update dumps).
6. Replay archived WAL segments for point-in-time recovery if needed (procedure to be written with the key ceremony).
7. Start the stack with `install.sh --fqdn <fqdn>`, which runs the migrator and the health check.

## Development and CI

- Validate locally without Docker: `bash deploy/ci/test-deploy.sh`, `bash deploy/ci/branding-check.sh`,
  `shellcheck deploy/install.sh deploy/ci/*.sh` and `shellcheck --shell=sh deploy/postgres/*.sh deploy/postgres/init/*.sh`.
- `deploy/install.sh` in a checkout contains a placeholder instead of the release public keys and refuses to install
  anything. Build a bundle with your own development release key to test on a scratch VPS:
  `deploy/ci/bundle-install.sh --version <x.y.z> --release-public-keys <base64 public key> --out dist/install.sh`, and
  serve a matching signed manifest (see `RELEASING.md`) through `FLEETIFY_RELEASE_BASE_URL`.
