-- Runs that are running, for the per-namespace limit on runs open at once (StoreLimits.MaxOpenRunsPerNamespace). The limit is checked by a bounded count of this index.
CREATE INDEX runs_open ON kairos.runs (tenant_id, namespace_id) WHERE state = 0;
