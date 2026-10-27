-- Credentials a cloud sign-in accepted once (a signed request, an ID token), remembered until they would be refused as too old anyway, so that a second presentation to ANY server instance is refused.
-- An entry is made only after the credential was verified and mapped to a scope, and carries that scope: like everything else it is under row level security, and a hash of the credential is all it holds.
CREATE TABLE kairos.sign_in_replays (
    tenant_id     uuid        NOT NULL,
    namespace_id  uuid        NOT NULL,
    token_hash    bytea       NOT NULL,        -- SHA-256 of the key the authenticator names the credential by
    expires_at    timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, namespace_id, token_hash)
);
CREATE INDEX sign_in_replays_expiry ON kairos.sign_in_replays (tenant_id, namespace_id, expires_at);

ALTER TABLE kairos.sign_in_replays ENABLE ROW LEVEL SECURITY;
ALTER TABLE kairos.sign_in_replays FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON kairos.sign_in_replays
    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid)
    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
       AND namespace_id = nullif(current_setting('app.namespace_id', true), '')::uuid);

GRANT SELECT, INSERT, UPDATE, DELETE ON kairos.sign_in_replays TO kairos_dataplane;
