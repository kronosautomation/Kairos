-- Kairos schema, v1. Applied by kairos_migrator (the only role that owns objects). Services never run DDL.
-- Every table carries tenant_id + namespace_id with FORCE ROW LEVEL SECURITY keyed on the per-transaction settings
-- app.tenant_id / app.namespace_id; an unset setting matches nothing (fails closed).

CREATE SCHEMA IF NOT EXISTS kairos;

CREATE TABLE kairos.runs (
    tenant_id       uuid        NOT NULL,
    namespace_id    uuid        NOT NULL,
    run_id          uuid        NOT NULL,
    workflow_id     text        NOT NULL,
    workflow_type   text        NOT NULL,
    build_id        text        NOT NULL,
    state           smallint    NOT NULL DEFAULT 0,        -- 0 running, 1 completed, 2 failed
    last_seq        bigint      NOT NULL,
    last_hash       bytea       NOT NULL,
    wf_lease_token  bigint      NOT NULL DEFAULT 0,        -- fencing token, never reset
    created_at      timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_id, run_id),
    UNIQUE (tenant_id, namespace_id, workflow_id)
);

-- Append-only, hash-chained. Partitioned by run so indexes stay small. 'body' is the canonical encoding (payloads inside are
-- opaque ciphertext once the Worker SDK encrypts client-side).
CREATE TABLE kairos.history_events (
    tenant_id       uuid   NOT NULL,
    namespace_id    uuid   NOT NULL,
    run_id          uuid   NOT NULL,
    seq             bigint NOT NULL,
    body            bytea  NOT NULL,
    prev_hash       bytea  NOT NULL,
    hash            bytea  NOT NULL,
    PRIMARY KEY (tenant_id, namespace_id, run_id, seq)
) PARTITION BY HASH (run_id);

DO $$
BEGIN
    FOR i IN 0..15 LOOP
        EXECUTE format('CREATE TABLE kairos.history_events_p%s PARTITION OF kairos.history_events FOR VALUES WITH (MODULUS 16, REMAINDER %s)', i, i);
    END LOOP;
END $$;

CREATE TABLE kairos.workflow_tasks (
    tenant_id         uuid        NOT NULL,
    namespace_id      uuid        NOT NULL,
    run_id            uuid        NOT NULL,
    build_id          text        NOT NULL,
    visible_at        timestamptz NOT NULL,
    leased            boolean     NOT NULL DEFAULT false,
    lease_expires_at  timestamptz,
    attempt           integer     NOT NULL DEFAULT 1,
    ord               bigint GENERATED ALWAYS AS IDENTITY,
    PRIMARY KEY (tenant_id, namespace_id, run_id)
);
CREATE INDEX workflow_tasks_poll ON kairos.workflow_tasks (tenant_id, namespace_id, ord);

CREATE TABLE kairos.activity_tasks (
    tenant_id         uuid        NOT NULL,
    namespace_id      uuid        NOT NULL,
    run_id            uuid        NOT NULL,
    activity_id       integer     NOT NULL,
    scheduled_body    bytea       NOT NULL,                -- canonical ActivityScheduled event body
    attempt           integer     NOT NULL DEFAULT 1,
    visible_at        timestamptz NOT NULL,
    leased            boolean     NOT NULL DEFAULT false,
    lease_expires_at  timestamptz,
    lease_token       bigint      NOT NULL DEFAULT 0,
    ord               bigint GENERATED ALWAYS AS IDENTITY,
    PRIMARY KEY (tenant_id, namespace_id, run_id, activity_id)
);
CREATE INDEX activity_tasks_poll ON kairos.activity_tasks (tenant_id, namespace_id, ord);

CREATE TABLE kairos.timers (
    tenant_id     uuid        NOT NULL,
    namespace_id  uuid        NOT NULL,
    run_id        uuid        NOT NULL,
    timer_id      integer     NOT NULL,
    due_at        timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_id, run_id, timer_id)
);
CREATE INDEX timers_due ON kairos.timers (tenant_id, namespace_id, due_at);

-- Control-plane tables: visible to kairos_controlplane only; the data plane has no grant on them.
CREATE TABLE kairos.tenants (
    tenant_id   uuid PRIMARY KEY,
    name        text        NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE kairos.namespaces (
    tenant_id     uuid        NOT NULL REFERENCES kairos.tenants,
    namespace_id  uuid        NOT NULL,
    name          text        NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, namespace_id)
);

-- Row level security ---------------------------------------------------------------------------------------------------

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['runs', 'history_events', 'workflow_tasks', 'activity_tasks', 'timers'] LOOP
        EXECUTE format('ALTER TABLE kairos.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kairos.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format($p$CREATE POLICY tenant_isolation ON kairos.%I
            USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
               AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid)
            WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
               AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid)$p$, t);
    END LOOP;

    FOREACH t IN ARRAY ARRAY['tenants', 'namespaces'] LOOP
        EXECUTE format('ALTER TABLE kairos.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE kairos.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('CREATE POLICY control_plane ON kairos.%I TO kairos_controlplane USING (true) WITH CHECK (true)', t);
    END LOOP;
END $$;

-- Privileges -----------------------------------------------------------------------------------------------------------

REVOKE ALL ON SCHEMA kairos FROM PUBLIC;
GRANT USAGE ON SCHEMA kairos TO kairos_dataplane, kairos_readonly, kairos_controlplane;

GRANT SELECT, INSERT, UPDATE, DELETE ON kairos.runs, kairos.workflow_tasks, kairos.activity_tasks, kairos.timers TO kairos_dataplane;
GRANT SELECT, INSERT ON kairos.history_events TO kairos_dataplane;   -- append-only: no UPDATE, no DELETE
GRANT SELECT ON kairos.runs, kairos.history_events, kairos.workflow_tasks, kairos.activity_tasks, kairos.timers TO kairos_readonly;
GRANT SELECT, INSERT, UPDATE, DELETE ON kairos.tenants, kairos.namespaces TO kairos_controlplane;
