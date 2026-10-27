-- How far each run's events have been handed to the deployment's event sink (a topic or queue). Like projected_seq it is moved by a compare-and-set after the sink accepted the events,
-- so delivery is at least once and every notice carries a stable id (run:sequence) for the consumer to de-duplicate. Runs that exist when this migration is applied are marked as
-- published: a deployment that turns the feed on starts with what happens next, not with a flood of what happened before.
ALTER TABLE kairos.runs ADD COLUMN published_seq bigint NOT NULL DEFAULT 0;
UPDATE kairos.runs SET published_seq = last_seq;
CREATE INDEX runs_unpublished ON kairos.runs (tenant_id, namespace_id) WHERE last_seq > published_seq;
