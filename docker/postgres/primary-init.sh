#!/bin/bash
# Creates the replication role and allows the replica to connect. Runs once on first start of pg-primary.
set -euo pipefail
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<SQL
CREATE ROLE replicator WITH REPLICATION LOGIN PASSWORD '${REPLICATION_PASSWORD}';
SQL
echo "host replication replicator all scram-sha-256" >> "$PGDATA/pg_hba.conf"
