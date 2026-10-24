-- Append-only record of privileged actions (resume a parked task, fail a run). Same tenant isolation as every other table.
CREATE TABLE kairos.audit_log (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id     uuid        NOT NULL,
    namespace_id  uuid        NOT NULL,
    run_id        uuid,
    at            timestamptz NOT NULL,
    action        text        NOT NULL,
    actor         text        NOT NULL,
    detail        text        NOT NULL
);
CREATE INDEX audit_log_scope ON kairos.audit_log (tenant_id, namespace_id, id);

ALTER TABLE kairos.audit_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE kairos.audit_log FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON kairos.audit_log
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid);

GRANT SELECT, INSERT ON kairos.audit_log TO kairos_dataplane;   -- append-only
GRANT SELECT ON kairos.audit_log TO kairos_readonly;
