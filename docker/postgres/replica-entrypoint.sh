#!/bin/bash
# Streaming replica of pg-primary. On first start, clones the primary with pg_basebackup, then runs as a hot standby.
# Failover drill: docker compose -f docker/compose.yml stop pg-primary
#                 docker compose -f docker/compose.yml exec pg-replica pg_ctl promote -D /var/lib/postgresql/data
set -euo pipefail
if [ ! -s "$PGDATA/PG_VERSION" ]; then
  until pg_isready -h pg-primary -p 5432; do echo "waiting for primary"; sleep 1; done
  rm -rf "${PGDATA:?}"/*
  PGPASSWORD="$REPLICATION_PASSWORD" pg_basebackup -h pg-primary -U replicator -D "$PGDATA" -Fp -Xs -P -R
  chmod 700 "$PGDATA"
fi
exec postgres -c hot_standby=on
