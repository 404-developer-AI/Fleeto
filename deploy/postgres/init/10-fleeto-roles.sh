#!/bin/sh
# Fleeto: database roles for one instance. The PostgreSQL image runs this once, when the data directory is created,
# as the postgres OS user over the local socket. install.sh copies it to /opt/fleeto/<instance>/postgres/init/.
#
# One login role per container with its own password (ARCHITECTURE.md section 5). fleeto_migrator owns the database and
# the schema and applies the per-role grants during `fleeto-tool migrate` (DatabaseGrants). Passwords are read
# by psql itself from the mounted Docker secrets, so they never appear on a command line or in a log.
set -eu

for file in db-migrator.password db-web.password db-gateway.password db-signer.password db-workers.password db-backup.password; do
    if [ ! -s "/run/secrets/$file" ]; then
        echo "fleeto init: secret /run/secrets/$file is missing or empty. Run install.sh again for this instance." >&2
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

CREATE ROLE fleeto_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE fleeto_web LOGIN PASSWORD :'web_password';
CREATE ROLE fleeto_gateway LOGIN PASSWORD :'gateway_password';
CREATE ROLE fleeto_signer LOGIN PASSWORD :'signer_password';
CREATE ROLE fleeto_workers LOGIN PASSWORD :'workers_password';
-- Read-only role for pg_dump in the nightly backup job.
CREATE ROLE fleeto_backup LOGIN PASSWORD :'backup_password';
GRANT pg_read_all_data TO fleeto_backup;

\unset migrator_password
\unset web_password
\unset gateway_password
\unset signer_password
\unset workers_password
\unset backup_password

ALTER DATABASE fleeto OWNER TO fleeto_migrator;

-- Only the Fleeto roles may connect, and only to the instance database.
REVOKE ALL ON DATABASE fleeto FROM PUBLIC;
REVOKE ALL ON DATABASE postgres FROM PUBLIC;
REVOKE ALL ON DATABASE template1 FROM PUBLIC;
GRANT CONNECT ON DATABASE fleeto TO fleeto_migrator, fleeto_web, fleeto_gateway, fleeto_signer, fleeto_workers, fleeto_backup;

\connect fleeto
ALTER SCHEMA public OWNER TO fleeto_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO fleeto_web, fleeto_gateway, fleeto_signer, fleeto_workers, fleeto_backup;

-- The image usually creates the extension already; the migrations rely on it for hypertables and compression.
CREATE EXTENSION IF NOT EXISTS timescaledb;
SQL

echo "fleeto init: roles and database fleeto are ready."
