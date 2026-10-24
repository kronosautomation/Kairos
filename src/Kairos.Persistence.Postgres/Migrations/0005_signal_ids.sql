-- Delivered signal ids per run: a redelivered signal (bus retry, client retry) is recognised and ignored.
CREATE TABLE kairos.signal_ids (
    tenant_id     uuid NOT NULL,
    namespace_id  uuid NOT NULL,
    run_id        uuid NOT NULL,
    signal_id     text NOT NULL,
    PRIMARY KEY (tenant_id, namespace_id, run_id, signal_id)
);

ALTER TABLE kairos.signal_ids ENABLE ROW LEVEL SECURITY;
ALTER TABLE kairos.signal_ids FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON kairos.signal_ids
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid);

GRANT SELECT, INSERT ON kairos.signal_ids TO kairos_dataplane;   -- append-only
GRANT SELECT ON kairos.signal_ids TO kairos_readonly;
