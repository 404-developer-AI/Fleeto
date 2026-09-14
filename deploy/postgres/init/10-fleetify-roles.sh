#!/bin/sh
# Fleeto: database roles for one instance. The PostgreSQL image runs this once, when the data directory is created,
# as the postgres OS user over the local socket. install.sh copies it to /opt/fleetify/<instance>/postgres/init/.
#
# One login role per container with its own password (ARCHITECTURE.md section 5). fleetify_migrator owns the database and
# the schema and applies the per-role grants during `fleetify-tool migrate` (DatabaseGrants). Passwords are read
# by psql itself from the mounted Docker secrets, so they never appear on a command line or in a log.
set -eu

for file in db-migrator.password db-web.password db-gateway.password db-signer.password db-workers.password db-backup.password; do
    if [ ! -s "/run/secrets/$file" ]; then
        echo "fleetify init: secret /run/secrets/$file is missing or empty. Run install.sh again for this instance." >&2
        exit 1
    fi
done

psql --no-psqlrc --set ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
\set QUIET on
\set migrator_password `cat /run/secrets/db-migrator.password`
\set web_password `cat /run/secrets/db-web.password`
\set gateway_password `cat /run/secrets/db-gateway.password`
\set signer_password `cat /run/secrets/db-signer.password`
\set workers_password `cat /run/secrets/db-workers.password`
\set backup_password `cat /run/secrets/db-backup.password`

CREATE ROLE fleetify_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE fleetify_web LOGIN PASSWORD :'web_password';
CREATE ROLE fleetify_gateway LOGIN PASSWORD :'gateway_password';
CREATE ROLE fleetify_signer LOGIN PASSWORD :'signer_password';
CREATE ROLE fleetify_workers LOGIN PASSWORD :'workers_password';
-- Read-only role for pg_dump in the nightly backup job.
CREATE ROLE fleetify_backup LOGIN PASSWORD :'backup_password';
GRANT pg_read_all_data TO fleetify_backup;

\unset migrator_password
\unset web_password
\unset gateway_password
\unset signer_password
\unset workers_password
\unset backup_password

ALTER DATABASE fleetify OWNER TO fleetify_migrator;

-- Only the Fleeto roles may connect, and only to the instance database.
REVOKE ALL ON DATABASE fleetify FROM PUBLIC;
REVOKE ALL ON DATABASE postgres FROM PUBLIC;
REVOKE ALL ON DATABASE template1 FROM PUBLIC;
GRANT CONNECT ON DATABASE fleetify TO fleetify_migrator, fleetify_web, fleetify_gateway, fleetify_signer, fleetify_workers, fleetify_backup;

\connect fleetify
ALTER SCHEMA public OWNER TO fleetify_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO fleetify_web, fleetify_gateway, fleetify_signer, fleetify_workers, fleetify_backup;

-- The image usually creates the extension already; the migrations rely on it for hypertables and compression.
CREATE EXTENSION IF NOT EXISTS timescaledb;
SQL

echo "fleetify init: roles and database fleetify are ready."
