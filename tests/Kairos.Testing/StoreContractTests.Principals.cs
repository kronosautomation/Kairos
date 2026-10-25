namespace Kairos.Testing;

/// <summary>
/// Resume, fail and cancel as an authenticated caller: the audit entry names the verified principal (never a supplied name), the scope is checked,
/// and cancel is one transaction of signal and audit entry.
/// </summary>
public abstract partial class StoreContractTests
{
    private static Principal Operator(TenantScope scope, string subject = "alice") => new(scope, Role.Operator, subject, null);

    private static Principal AdminOf(TenantScope scope, string subject = "root") => new(scope, Role.Admin, subject, null);

    [Fact]
    public async Task A_verified_principal_is_what_the_audit_entry_names_for_resume_and_fail()
    {
        (IKairosStore store, KairosClient client, RunKey parked) = await ParkedRunAsync();
        StartRunResult other = await client.StartAsync(A, "second", "wf", [], cancellationToken: Ct);
        var second = new RunKey(A.TenantId, A.NamespaceId, other.RunId);

        Assert.True(await store.ResumeWorkflowTaskVerifiedAsync(parked, AdminOf(A, "root@example.com"), Time.GetUtcNow(), Ct));
        Assert.True(await store.FailRunVerifiedAsync(second, "stopped for the test", Operator(A), Time.GetUtcNow(), Ct));
        // The string forms are a claim: they are recorded as such and never become the actor.
        StartRunResult third = await client.StartAsync(A, "third", "wf", [], cancellationToken: Ct);
        Assert.True(await store.FailRunAsync(new RunKey(A.TenantId, A.NamespaceId, third.RunId), "claimed", "ceo@example.com", Time.GetUtcNow(), Ct));

        IReadOnlyList<AuditEntry> audit = await store.GetAuditLogAsync(A, Ct);
        Assert.Equal(3, audit.Count);
        Assert.Equal(("ResumeWorkflowTask", "Admin:root@example.com", "Admin:root@example.com", null), (audit[0].Action, audit[0].Actor, audit[0].VerifiedPrincipal, audit[0].ClaimedActor));
        Assert.Equal(("FailRun", "Operator:alice", "Operator:alice", null), (audit[1].Action, audit[1].Actor, audit[1].VerifiedPrincipal, audit[1].ClaimedActor));
        Assert.Equal("FailRun", audit[2].Action);
        Assert.Equal("ceo@example.com", audit[2].ClaimedActor);
        Assert.Null(audit[2].VerifiedPrincipal);
        Assert.Equal("db:" + audit[2].SessionUser, audit[2].Actor);
        Assert.DoesNotContain("ceo", audit[2].Actor, StringComparison.Ordinal);
        Assert.All(audit, a => Assert.False(string.IsNullOrEmpty(a.SessionUser), "the database login is recorded by the store"));
    }

    [Fact]
    public async Task A_principal_from_another_scope_is_refused_and_nothing_changes()
    {
        (IKairosStore store, KairosClient client, RunKey parked) = await ParkedRunAsync();
        StartRunResult started = await client.StartAsync(A, "target", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, started.RunId);

        foreach (TenantScope foreign in new[] { B, new TenantScope(A.TenantId, B.NamespaceId), new TenantScope(B.TenantId, A.NamespaceId) })
        {
            Principal mallory = AdminOf(foreign, "mallory");
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.ResumeWorkflowTaskVerifiedAsync(parked, mallory, Time.GetUtcNow(), Ct));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.FailRunVerifiedAsync(run, "x", mallory, Time.GetUtcNow(), Ct));
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.CancelRunAsync(run, "x", mallory, "", Time.GetUtcNow(), Ct));
        }

        Assert.Empty(await store.GetAuditLogAsync(A, Ct));
        Assert.Equal(RunState.Running, await client.GetStateAsync(run, Ct));
        Assert.Single(await store.GetStuckWorkflowTasksAsync(A, Ct));
    }

    [Fact]
    public async Task Cancel_sends_one_empty_cancel_signal_and_audits_who_asked_in_the_same_step()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "stoppable", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);

        Assert.True(await store.CancelRunAsync(run, "customer asked", Operator(A, "alice"), "", Time.GetUtcNow(), Ct));
        Assert.True(await store.CancelRunAsync(run, "asked again", Operator(A, "bob"), "", Time.GetUtcNow(), Ct)); // already asked: still open, so still true

        IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(run, Ct);
        SignalReceived cancel = Assert.Single(history.OfType<SignalReceived>());
        Assert.Equal((ReservedSignals.Cancel, ReservedSignals.CancelSignalId, 0), (cancel.SignalName, cancel.SignalId, cancel.Payload.Length));
        AssertChainIntact(history);

        IReadOnlyList<AuditEntry> audit = await store.GetAuditLogAsync(A, Ct);
        Assert.Equal(["CancelRun", "CancelRun"], audit.Select(a => a.Action));
        Assert.Equal(["Operator:alice", "Operator:bob"], audit.Select(a => a.Actor));
        Assert.Equal(["customer asked", "asked again"], audit.Select(a => a.Detail));
        Assert.All(audit, a => Assert.Equal(a.Actor, a.VerifiedPrincipal));
    }

    [Fact]
    public async Task Cancel_of_a_closed_or_unknown_run_records_nothing()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "done-soon", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        await DriveAsync(store, A, Workflows("default", (_, _) => Task.FromResult(B8("done"))), Activities());
        Assert.Equal(RunState.Completed, await client.GetStateAsync(run, Ct));

        Assert.False(await store.CancelRunAsync(run, "too late", Operator(A), "", Time.GetUtcNow(), Ct));
        Assert.False(await store.CancelRunAsync(new RunKey(A.TenantId, A.NamespaceId, Guid.NewGuid()), "who", Operator(A), "", Time.GetUtcNow(), Ct));
        Assert.Empty(await store.GetAuditLogAsync(A, Ct));
        Assert.Empty((await client.GetHistoryAsync(run, Ct)).OfType<SignalReceived>());
    }

    [Fact]
    public async Task A_reason_longer_than_the_bound_is_refused_before_anything_is_written()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "w", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await store.CancelRunAsync(run, new string('x', StoreRules.MaxReasonLength + 1), Operator(A), "", Time.GetUtcNow(), Ct));

        Assert.Empty(await store.GetAuditLogAsync(A, Ct));
        Assert.Empty((await client.GetHistoryAsync(run, Ct)).OfType<SignalReceived>());
    }

    [Fact]
    public async Task A_workflow_waiting_on_the_cancel_signal_stops_when_the_store_cancels_it()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "waiting", "wf", [], cancellationToken: Ct);
        var run = new RunKey(A.TenantId, A.NamespaceId, start.RunId);
        WorkflowRegistry wf = Workflows("default", async (ctx, _) =>
        {
            byte[] reason = await ctx.WaitForSignalAsync(ReservedSignals.Cancel);
            return B8("stopped:" + reason.Length);
        });
        await DriveAsync(store, A, wf, Activities());
        Assert.Equal(RunState.Running, await client.GetStateAsync(run, Ct));

        Assert.True(await store.CancelRunAsync(run, "stop", Operator(A), "", Time.GetUtcNow(), Ct));
        await DriveAsync(store, A, wf, Activities());

        Assert.Equal(RunState.Completed, await client.GetStateAsync(run, Ct));
        Assert.Equal("stopped:0", Str(Assert.IsType<WorkflowCompleted>((await client.GetHistoryAsync(run, Ct))[^1]).Result));
    }
}
