-- Continue-as-new: several runs (a chain) share one workflow id; only the latest is current.
-- State 3 = continued as new. The business id is unique among current runs only.
ALTER TABLE kairos.runs ADD COLUMN is_current boolean NOT NULL DEFAULT true;
ALTER TABLE kairos.runs DROP CONSTRAINT runs_tenant_id_namespace_id_workflow_id_key;
CREATE UNIQUE INDEX runs_current_workflow_id ON kairos.runs (tenant_id, namespace_id, workflow_id) WHERE is_current;
