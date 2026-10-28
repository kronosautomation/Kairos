-- Which scopes have run events nobody has published, so that a node can publish them even when no worker of the scope is polling it (a run a client started, a run whose workers have all gone).
--
-- Row level security hides every scope from every other, and a publisher cannot name scopes it cannot see. This adds the one narrow exception, in the same form as the policy store's reconcile sweep:
-- a data-plane session that sets app.sweep = 'publish-events' may SELECT (never write) the runs that still have events to publish, in every scope. The store's one sweep query sets the marker (an
-- architecture test holds that) and takes scope ids from it and nothing else; the events themselves are then read and published scope by scope, under the ordinary policy.
--
-- What this does not change: a data-plane session could already set app.tenant_id to any value, so this does not give the role a power it lacked; it makes the one cross-scope read explicit, named,
-- and limited to runs that are about to be published anyway.
CREATE POLICY unpublished_sweep ON kairos.runs
    FOR SELECT TO kairos_dataplane
    USING (current_setting('app.sweep', true) = 'publish-events' AND last_seq > published_seq);
