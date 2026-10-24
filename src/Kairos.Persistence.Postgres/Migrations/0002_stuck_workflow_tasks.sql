-- A workflow task whose lease expires too many times is parked, not dropped: it stays visible (stuck = true) and is never offered.
ALTER TABLE kairos.workflow_tasks ADD COLUMN stuck boolean NOT NULL DEFAULT false;
