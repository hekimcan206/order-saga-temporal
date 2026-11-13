#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d).
# Least privilege:
#   saga_migrator  owns the "sim" and "saga_admin" schemas and runs migrations (DDL);
#   saga_app       is what the workers and the API connect as: DML on "sim" only, which the migrations narrow
#                  further (the payment ledger is append-only: SELECT and INSERT).
#   "saga_admin" holds the migration history; the application role cannot see it.
# Temporal keeps its own state in the Temporal service; this database only holds the simulated downstream systems.
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" \
     -v app_password="$APP_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE saga_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE saga_app LOGIN PASSWORD :'app_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO saga_migrator, saga_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE SCHEMA sim AUTHORIZATION saga_migrator;
CREATE SCHEMA saga_admin AUTHORIZATION saga_migrator;
GRANT USAGE ON SCHEMA sim TO saga_app;

ALTER DEFAULT PRIVILEGES FOR ROLE saga_migrator IN SCHEMA sim
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO saga_app;
SQL
