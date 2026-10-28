-- What workers of a namespace say about their payloads: the last time one reported that it stores them readable (AllowUnencryptedPayloads), and the last time one reported that it encrypts them.
-- The dashboard shows a namespace with a recent readable report. Like everything else it is under row level security.
CREATE TABLE kairos.worker_posture (
    tenant_id             uuid        NOT NULL,
    namespace_id          uuid        NOT NULL,
    readable_seen_at      timestamptz,
    protected_seen_at     timestamptz,
    PRIMARY KEY (tenant_id, namespace_id)
);

ALTER TABLE kairos.worker_posture ENABLE ROW LEVEL SECURITY;
ALTER TABLE kairos.worker_posture FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON kairos.worker_posture
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid);

GRANT SELECT, INSERT, UPDATE ON kairos.worker_posture TO kairos_dataplane;
GRANT SELECT ON kairos.worker_posture TO kairos_readonly;
