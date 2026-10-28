-- The last time a worker of the namespace said it accepts approval decisions without verifying who made them (AllowUnverifiedApprovers). Shown by the dashboard like the payload posture.
ALTER TABLE kairos.worker_posture ADD COLUMN unverified_approvers_seen_at timestamptz;
