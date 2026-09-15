# Fleeto deployment

Everything needed to run Fleeto on an Ubuntu VPS: the per-instance Compose stack, the host proxy, and `install.sh`,
which installs and updates instances. Design background: `MD-Files/ARCHITECTURE.md` sections 1, 5 and 7.

| Path | Purpose |
|---|---|
| `install.sh` | Installs and updates instances. The release build embeds the files below and the Steaan release public keys. |
| `compose/compose.yml` | Template of one instance stack (postgres, migrator, signer, gateway, workers, web). |
| `postgres/init/10-fleeto-roles.sh` | Creates the database roles at first start of an instance database. |
| `postgres/archive-wal.sh` | PostgreSQL `archive_command`: copies WAL segments into the spool the workers upload from. |
| `caddy/Dockerfile`, `caddy/compose.yml` | Host proxy image (Caddy + layer4 module) and its Compose file. |
| `ci/bundle-install.sh` | Produces the release `install.sh` (version, public keys, embedded templates). |
| `ci/test-deploy.sh` | Self-test of the bundle, signature checks, Caddyfile generation and Compose rendering. |
| `ci/branding-check.sh` | Fails on the old internal name outside the migration files, and on "device" or "machine" in user-visible UI text. |
| `release-rollback` | Rollback mode (`images` or `restore`) of the next release. |
| `RELEASING.md` | How a release is built, signed offline and published. |

## VPS requirements

- Ubuntu 24.04 or 22.04, amd64, root access, a public IPv4 address (IPv6 optional).
- Inbound TCP 80 and 443 open (80 for certificate issuance and redirects; 443 for the web UI, the API and agents).
  Nothing else needs to be reachable from outside: every instance port is bound to 127.0.0.1.
- Behind a firewall or NAT the DNS records point to the public address that forwards TCP 80 and 443 to the VPS, which
  may differ from the address the VPS uses outbound. Use a plain port forward that keeps the client address, so agents
  are shown with their own public address. install.sh asks once to confirm such an address and stores it in
  `/opt/fleeto/public-addresses`.
- Any uplink MTU from 1280 to 1500 works (a 1400 link, a tunnel): install.sh reads the MTU of the interface of the default
  route on every install and update, stores it as `NETWORK_MTU` in `instance.conf` and creates the instance networks with
  it, so containers never send larger packets than the uplink carries and nothing depends on "packet too big" messages
  or MSS clamping. When it changes, the next update recreates the networks (the instance restarts; volumes stay). The
  host proxy uses the host network and so the uplink MTU already.
- Outbound HTTPS to `api.github.com` and GitHub's download hosts (`*.githubusercontent.com`), `ghcr.io`, Docker Hub,
  `download.docker.com`, the Ubuntu mirrors, Let's Encrypt, and the backup storage and SMTP server of each instance.
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

## GitHub tokens

Steaan runs every instance, and releases are GitHub Releases of the private repository (`RELEASING.md`). install.sh asks
once per VPS for two read-only tokens and stores them in `/opt/fleeto/credentials/` (0700 root, files 0600):

| Token | Create at | Settings | Used for |
|---|---|---|---|
| Release token | GitHub, Settings, Developer settings, Fine-grained tokens | Repository access: only `404-developer-AI/Fleeto`; Permissions: Contents read-only (Metadata read-only is added automatically) | The list of releases, `manifest.json`, `install.sh` and their signatures |
| Packages token | GitHub, Settings, Developer settings, Tokens (classic) | Only the `read:packages` scope | Pulling the images from ghcr.io (GitHub Packages accepts no fine-grained tokens) |

- install.sh checks both when they are entered: the release token must read the releases, the packages token must have
  `read:packages` (it warns about any extra scope). It warns 30 days before a token expires; replace tokens with
  `sudo /opt/fleeto/bin/install.sh --github-tokens`.
- The registry login exists only while install.sh runs (a Docker config in its temporary directory); no registry
  credential stays on disk.
- Residual risk: the release token can read the repository contents, so whoever controls the VPS as root can read the
  source code. Use tokens that expire, one pair per VPS, and revoke them when a VPS is retired.
- Signatures still decide: a release file or image that is not covered by a manifest signed with the Steaan release key
  is never used, whatever GitHub serves.

## First install

Download install.sh from the release directly on the VPS, as root, with the release token (it goes to curl in a
root-only header file, never on a command line or in the shell history):

```bash
mkdir -p /root/fleeto && cd /root/fleeto
apt-get update && apt-get install -y curl jq openssl
read -rsp 'Release token: ' GH_TOKEN; echo
(umask 077; printf 'Authorization: Bearer %s\n' "$GH_TOKEN" > .auth); unset GH_TOKEN
api=https://api.github.com/repos/404-developer-AI/Fleeto
curl -fsSL -H @.auth -H 'Accept: application/vnd.github+json' "$api/releases/tags/v<version>" > release.json
for name in install.sh install.sh.sig; do
  url=$(jq -r --arg n "$name" '.assets[] | select(.name == $n) | .url' release.json)
  curl -fsSL -H @.auth -H 'Accept: application/octet-stream' -o "$name" "$url"
done
rm -f .auth release.json
```

Build the Steaan release public key from its base64 form, taken from the key ceremony record and never from GitHub
(whoever could change the release could change a key published next to it), and verify the script:

```bash
{ printf '\x30\x2a\x30\x05\x06\x03\x2b\x65\x70\x03\x21\x00'; printf '%s' '<base64 release public key>' | base64 -d; } \
  | openssl pkey -pubin -inform DER -out steaan-release.pub
openssl pkeyutl -verify -rawin -pubin -inkey steaan-release.pub -in install.sh -sigfile install.sh.sig
bash install.sh
```

The prefix bytes are the fixed DER header of an ed25519 public key. From a workstation with the GitHub CLI,
`gh release download v<version> --repo 404-developer-AI/Fleeto --pattern 'install.sh*'` and `scp` work as well.

install.sh asks for the FQDN of the instance and for the two GitHub tokens. The first run on a VPS installs the required
packages, Docker Engine (from Docker's apt repository, key fingerprint checked) and the host proxy. For the instance it
then:

1. checks DNS, verifies the release manifest and pulls every image by digest;
2. creates `/opt/fleeto/<instance>/` and generates `root.key`, `signer.key` and one database password per role;
3. starts PostgreSQL, runs the migrator, starts the stack and waits until every service is healthy;
4. adds the HTTPS route and the SNI passthrough route to the host proxy (the passthrough sends a PROXY protocol v2
   header, so the gateway sees the agent's address; the gateway trusts that header only from the private Docker ranges
   set in `compose/compose.yml`);
5. prints the URL, the one-time first-admin setup link (valid 24 hours) and the key ceremony reminder.

Running it again is safe: an interrupted install continues, existing secrets are never overwritten.

### Layout on the VPS

```
/opt/fleeto/                         0700 root
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
sudo /opt/fleeto/bin/install.sh --fqdn rmm.customer.example              # to the latest release
sudo /opt/fleeto/bin/install.sh --fqdn rmm.customer.example --version 0.1.1
sudo /opt/fleeto/bin/install.sh --all                                    # every instance on the VPS
sudo /opt/fleeto/bin/install.sh --check                                  # installed versus latest, no changes
sudo /opt/fleeto/bin/install.sh --list
```

An update verifies the new manifest (and switches to the install.sh listed in it), pulls the images, dumps the
database to `backups/`, runs the migrator, restarts the stack and runs the health check (container health, web
`/health`, a TLS handshake with the gateway). When anything fails it rolls back to the previous images; for a release
marked `rollback: restore` it first restores the pre-update dump, and it asks for confirmation (or `--yes`) before
such an update starts. install.sh never downgrades an instance.

### Moving from the Fleetify layout (0.2.1)

A VPS installed before 0.2.1 uses the old internal name: `/opt/fleetify`, projects `fleetify-*`, database `fleetify`. The
first update to 0.2.1 or later moves it (MD-Files/ARCHITECTURE.md §7, Rename to Fleeto):

```
sudo /opt/fleetify/bin/install.sh --all          # first attempt: the installed install.sh hands over to the new one
sudo /opt/fleeto/bin/install.sh --all            # every later run, also a retry after a failed move
```

- It asks once for confirmation (or `--yes`): every instance on the VPS is stopped while its data is copied, then updated.
  Plan it like an update of every instance at once; the copies need free disk space for the volumes (checked first).
- The old layout stays untouched until an instance runs the new release; an instance whose update fails runs again from
  `/opt/fleetify` and moves with the next run. Once the host proxy has moved (the first attempt usually gets that far),
  `/opt/fleetify/bin/install.sh` only points to `/opt/fleeto/bin/install.sh`, so retry with the latter. Afterwards `/opt/fleetify` and the `fleetify-*` volumes are gone, and the
  install.sh to use is `/opt/fleeto/bin/install.sh`. The pre-rename dump is in `/opt/fleeto/<instance>/backups/`.
- Endpoints with a Fleetify agent (0.2.0) keep reporting but take no new configuration or jobs until the install command
  of their site is run again on them; it takes the agent over with its enrollment (no new endpoint, no token used).

## Backups

- Nightly `pg_dump` and continuous WAL archiving are done by fleeto-workers, encrypted with the instance's backup
  public key before upload to S3-compatible storage in the EU. Configure the destination and the backup public key in
  Settings, Backups. Until then the dashboard warns and WAL segments accumulate in `wal-spool/`.
- The pre-update dumps in `backups/` are a rollback aid only. They are not encrypted and never leave the VPS.
- Instance secrets (`secrets/`) are never part of a backup.

## Key ceremony

Do this on the day an instance is installed. The full procedure is part of the key ceremony document planned in
`MD-Files/ARCHITECTURE.md` section 5; the minimum:

1. Copy `secrets/root.key` and `secrets/signer.key` to two offline locations (for example two encrypted USB drives
   kept in different places). Record who holds them.
2. On an offline machine, create the backup key pair: `fleeto-tool backup keygen --out <folder>`. Keep
   `backup.key` offline next to the instance keys; paste the public key into Settings, Backups.
3. Store read credentials for the backup destination with the offline keys.
4. Rehearse a restore (below) onto a scratch VPS before relying on the backups.

## Restore (outline)

A restore needs the offline backup private key, the original `root.key` and `signer.key`, and read access to the
backup destination.

1. Prepare a VPS with DNS for the same FQDN and run `install.sh --fqdn <fqdn>` once so the stack and secrets exist.
2. Stop the application services: `docker compose -p fleeto-<instance> --env-file instance.conf -f compose.yml stop web gateway workers signer migrator`.
3. Replace `secrets/root.key` and `secrets/signer.key` with the offline originals (mode 0440, root:10001).
4. Download the latest nightly dump and decrypt it offline: `fleeto-tool backup decrypt --key backup.key --in <file> --out fleeto.dump`.
5. Recreate the database and restore, as the postgres superuser inside the postgres container:
   `DROP DATABASE fleeto WITH (FORCE); CREATE DATABASE fleeto OWNER fleeto_migrator;`, grant CONNECT to the
   fleeto roles, `CREATE EXTENSION timescaledb; SELECT timescaledb_pre_restore();`, then
   `pg_restore --dbname=fleeto fleeto.dump` and `SELECT timescaledb_post_restore();`
   (`restore_database` in install.sh does exactly this for pre-update dumps).
6. Replay archived WAL segments for point-in-time recovery if needed (procedure to be written with the key ceremony).
7. Start the stack with `install.sh --fqdn <fqdn>`, which runs the migrator and the health check.

## Development and CI

- Validate locally without Docker: `bash deploy/ci/test-deploy.sh`, `bash deploy/ci/branding-check.sh`,
  `shellcheck deploy/install.sh deploy/ci/*.sh` and `shellcheck --shell=sh deploy/postgres/*.sh deploy/postgres/init/*.sh`.
- `deploy/install.sh` in a checkout contains a placeholder instead of the release public keys and refuses to install
  anything. Test on a scratch VPS with a pre-release built and signed with test keys (`RELEASING.md`);
  `FLEETO_RELEASE_REPOSITORY=<owner>/<repo>` points install.sh at the releases of another repository, such as a fork.
