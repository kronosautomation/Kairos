-- What the server can answer without opening a payload: model usage by workflow type and day, and the approvals runs are waiting for. Both are projected from the run's history
-- (readable metadata only: usage counts, markers the agent runtime leaves) by the store's projector, outside the hot path, and read here. A run's rows are replaced as a whole when
-- it is projected, so they always equal what scanning its history gives; projected_seq says how far the projector has read.
ALTER TABLE kairos.runs ADD COLUMN projected_seq bigint NOT NULL DEFAULT 0;
CREATE INDEX runs_unprojected ON kairos.runs (tenant_id, namespace_id) WHERE last_seq > projected_seq;

CREATE TABLE kairos.usage_by_run (
    tenant_id                  uuid   NOT NULL,
    namespace_id               uuid   NOT NULL,
    run_id                     uuid   NOT NULL,
    day                        date   NOT NULL,   -- the UTC day of the recorded attempt
    model                      text   NOT NULL,
    steps                      bigint NOT NULL,   -- completed model steps
    failed_attempts            bigint NOT NULL,   -- attempts that were paid for and then failed
    input_tokens               bigint NOT NULL,
    output_tokens              bigint NOT NULL,
    cached_input_tokens        bigint NOT NULL,
    saved_input_tokens         bigint NOT NULL,   -- of the above, completed before a later failure of the run: not paid for again when it carried on
    saved_output_tokens        bigint NOT NULL,
    saved_cached_input_tokens  bigint NOT NULL,
    PRIMARY KEY (tenant_id, namespace_id, run_id, day, model)
);
CREATE INDEX usage_by_run_day ON kairos.usage_by_run (tenant_id, namespace_id, day);

CREATE TABLE kairos.pending_approvals (
    tenant_id     uuid        NOT NULL,
    namespace_id  uuid        NOT NULL,
    run_id        uuid        NOT NULL,
    approval_id   text        NOT NULL,
    requested_at  timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_id, run_id, approval_id)
);
CREATE INDEX pending_approvals_age ON kairos.pending_approvals (tenant_id, namespace_id, requested_at, approval_id);

ALTER TABLE kairos.usage_by_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE kairos.usage_by_run FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON kairos.usage_by_run
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid);

ALTER TABLE kairos.pending_approvals ENABLE ROW LEVEL SECURITY;
ALTER TABLE kairos.pending_approvals FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON kairos.pending_approvals
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid);

GRANT SELECT, INSERT, UPDATE, DELETE ON kairos.usage_by_run, kairos.pending_approvals TO kairos_dataplane;
GRANT SELECT ON kairos.usage_by_run, kairos.pending_approvals TO kairos_readonly;
