namespace Kairos.Testing;

/// <summary>
/// Tenant isolation for every method of <see cref="IKairosStore"/>. A rich tenant A (a leased workflow task, a leased activity, a timer, a
/// parked task, an audit entry, a closed run) is attacked through each method from three foreign scopes: another tenant and
/// namespace, the right tenant with the wrong namespace, and the wrong tenant with the right namespace. Each call must fail
/// closed (empty, false, null or StaleLease; never data), change nothing of A's, leak none of A's identifiers in errors, and
/// leave A's legitimate holders able to finish. The cases are keyed by method name and checked against the interface by
/// reflection, so a new store method without a leak case fails this suite.
/// </summary>
public abstract partial class StoreContractTests
{
    private sealed record Arena(
        IKairosStore Store, KairosClient Client, RunKey Parked, RunKey Finished, RunKey Leased, RunKey Busy,
        WorkflowTask WorkflowTask, ActivityTask ActivityTask, string[] WorkflowIds);

    private static readonly TenantScope MixedNamespace = new(A.TenantId, B.NamespaceId);
    private static readonly TenantScope MixedTenant = new(B.TenantId, A.NamespaceId);

    public static TheoryData<string> StoreMethodNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in typeof(IKairosStore).GetMethods().Select(m => m.Name).Distinct().Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    private async Task<Arena> BuildArenaAsync()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();

        // Two runs whose workflow task keeps expiring until the store parks them; one is resumed (an audit entry) and finishes.
        StartRunResult p1 = await client.StartAsync(A, "leak-parked-1", "quick", [], cancellationToken: Ct);
        StartRunResult p2 = await client.StartAsync(A, "leak-parked-2", "quick", [], cancellationToken: Ct);
        for (int i = 0; i < StoreRules.MaxWorkflowTaskAttempts + 2; i++)
        {
            await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct);
            await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromSeconds(30), Ct);
            Time.Advance(TimeSpan.FromSeconds(31));
            await store.ReapExpiredLeasesAsync(A, Time.GetUtcNow(), Ct);
            Time.Advance(TimeSpan.FromSeconds(70));
        }

        var parked1 = new RunKey(A.TenantId, A.NamespaceId, p1.RunId);
        var parked2 = new RunKey(A.TenantId, A.NamespaceId, p2.RunId);
        Assert.True(await store.ResumeWorkflowTaskAsync(parked1, "ops@a.example", Time.GetUtcNow(), Ct));

        WorkflowRegistry registry = new WorkflowRegistry()
            .Register("quick", "default", (_, _) => Task.FromResult(B8("done")))
            .Register("busy", "default", async (ctx, input) =>
            {
                Task<byte[]> work = ctx.ExecuteActivityAsync("echo", input, RetryPolicy.Default with { MaximumAttempts = 3 });
                await ctx.DelayAsync(TimeSpan.FromHours(1));
                return await work;
            });
        var worker = new WorkerRuntime(store, A, registry, Activities(), Time, new WorkerOptions { AutoHeartbeat = false });
        await worker.ProcessWorkflowTaskAsync(Ct); // parked1 finishes (parked2 stays parked)

        StartRunResult busy = await client.StartAsync(A, "leak-busy", "busy", B8("SECRET-CANARY"), cancellationToken: Ct);
        await worker.ProcessWorkflowTaskAsync(Ct); // schedules an activity and a one-hour timer
        ActivityTask activity = (await store.PollActivityTaskAsync(A, Time.GetUtcNow(), TimeSpan.FromMinutes(10), Ct))!;

        StartRunResult leased = await client.StartAsync(A, "leak-leased", "idle", [], cancellationToken: Ct);
        WorkflowTask task = (await store.PollWorkflowTaskAsync(A, DefaultBuild, Time.GetUtcNow(), TimeSpan.FromMinutes(10), Ct))!;
        Assert.Equal(leased.RunId, task.Run.RunId);

        return new Arena(
            store, client, parked2, parked1, new RunKey(A.TenantId, A.NamespaceId, leased.RunId), new RunKey(A.TenantId, A.NamespaceId, busy.RunId),
            task, activity, ["leak-parked-1", "leak-parked-2", "leak-busy", "leak-leased"]);
    }

    private async Task<string> SnapshotAsync(Arena a)
    {
        var sb = new System.Text.StringBuilder();
        foreach (RunKey run in new[] { a.Parked, a.Finished, a.Leased, a.Busy })
        {
            IReadOnlyList<HistoryEvent> history = await a.Store.GetHistoryAsync(run, Ct);
            sb.Append(run.RunId).Append(':').Append(await a.Store.GetRunStateAsync(run, Ct)).Append(':').Append(history.Count)
              .Append(':').Append(history.Count == 0 ? "" : Convert.ToHexString(history[^1].Hash)).Append('\n');
        }

        StoreStats stats = await StatsAsync(a.Store, A);
        sb.Append(stats).Append('\n');
        foreach (StuckWorkflowTask stuck in await a.Store.GetStuckWorkflowTasksAsync(A, Ct))
        {
            sb.Append("stuck ").Append(stuck.Run.RunId).Append(' ').Append(stuck.Attempts).Append('\n');
        }

        foreach (AuditEntry audit in await a.Store.GetAuditLogAsync(A, Ct))
        {
            sb.Append("audit ").Append(audit.Action).Append(' ').Append(audit.Actor).Append(' ').Append(audit.RunId).Append('\n');
        }

        foreach (string id in a.WorkflowIds)
        {
            sb.Append("current ").Append(id).Append('=').Append((await a.Store.GetCurrentRunAsync(A, id, Ct))?.RunId).Append('\n');
        }

        return sb.ToString();
    }

    private static RunKey In(TenantScope foreign, RunKey run) => new(foreign.TenantId, foreign.NamespaceId, run.RunId);

    private static void AssertNoIdentifiersLeak(Arena a, string message)
    {
        foreach (string secret in new[]
        {
            A.TenantId.ToString(), A.NamespaceId.ToString(), a.Parked.RunId.ToString(), a.Finished.RunId.ToString(),
            a.Leased.RunId.ToString(), a.Busy.RunId.ToString(), "SECRET-CANARY", "leak-busy", "leak-parked",
        })
        {
            Assert.DoesNotContain(secret, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The foreign call for each store method. Each asserts its own result; the driver checks A is untouched.</summary>
    private Dictionary<string, Func<Arena, TenantScope, Task>> LeakCases() => new(StringComparer.Ordinal)
    {
        [nameof(IKairosStore.StartRunAsync)] = async (a, f) =>
        {
            // Same business id in a foreign scope is a different workflow: never a dedup hit that reveals or returns A's run.
            StartRunResult r = await a.Store.StartRunAsync(
                new StartRunRequest(new RunKey(f.TenantId, f.NamespaceId, Guid.NewGuid()), "leak-busy", "wf", "default", B8("x"), Time.GetUtcNow()), Ct);
            Assert.True(r.Started);
            Assert.NotEqual(a.Busy.RunId, r.RunId);
            Assert.Equal(a.Busy.RunId, (await a.Store.GetCurrentRunAsync(A, "leak-busy", Ct))!.Value.RunId);

            // A foreign caller reusing A's run id creates its own run in its own scope and cannot touch A's.
            StartRunResult reused = await a.Store.StartRunAsync(
                new StartRunRequest(In(f, a.Busy), "reuse-" + Guid.NewGuid(), "wf", "default", [], Time.GetUtcNow()), Ct);
            Assert.True(reused.Started);
            Assert.Equal(RunState.Running, await a.Store.GetRunStateAsync(a.Busy, Ct));
        },
        [nameof(IKairosStore.SignalRunAsync)] = async (a, f) =>
            Assert.False(await a.Store.SignalRunAsync(In(f, a.Busy), "go", "leak-signal", B8("x"), Time.GetUtcNow(), Ct)),
        [nameof(IKairosStore.SignalRunTracedAsync)] = async (a, f) =>
            Assert.False(await a.Store.SignalRunTracedAsync(In(f, a.Busy), "go", "leak-signal-traced", B8("x"), Time.GetUtcNow(), "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", Ct)),
        [nameof(IKairosStore.PollWorkflowTaskAsync)] = async (a, f) =>
            Assert.Null(await a.Store.PollWorkflowTaskAsync(f, DefaultBuild, Time.GetUtcNow().AddDays(1), TimeSpan.FromMinutes(1), Ct)),
        [nameof(IKairosStore.CompleteWorkflowTaskAsync)] = async (a, f) =>
        {
            WorkflowTask hijack = a.WorkflowTask with { Run = In(f, a.Leased) };
            var done = new WorkflowCompleted(f.TenantId, f.NamespaceId, a.Leased.RunId, 0, Time.GetUtcNow(), B8("stolen"));
            Assert.Equal(CompletionOutcome.StaleLease, await a.Store.CompleteWorkflowTaskAsync(hijack, [done], Time.GetUtcNow(), Ct));

            // Events that name A while the task names the foreign scope are refused outright, without echoing A's identifiers.
            var asA = new WorkflowCompleted(A.TenantId, A.NamespaceId, a.Leased.RunId, 0, Time.GetUtcNow(), []);
            ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(async () =>
                await a.Store.CompleteWorkflowTaskAsync(hijack, [asA], Time.GetUtcNow(), Ct));
            AssertNoIdentifiersLeak(a, refused.Message);
        },
        [nameof(IKairosStore.ReleaseWorkflowTaskAsync)] = async (a, f) =>
            await a.Store.ReleaseWorkflowTaskAsync(a.WorkflowTask with { Run = In(f, a.Leased) }, Time.GetUtcNow(), Time.GetUtcNow(), countAttempt: true, Ct),
        [nameof(IKairosStore.PollActivityTaskAsync)] = async (a, f) =>
            Assert.Null(await a.Store.PollActivityTaskAsync(f, Time.GetUtcNow().AddDays(1), TimeSpan.FromMinutes(1), Ct)),
        [nameof(IKairosStore.HeartbeatActivityTaskAsync)] = async (a, f) =>
            Assert.Equal(CompletionOutcome.StaleLease, await a.Store.HeartbeatActivityTaskAsync(
                a.ActivityTask with { Run = In(f, a.Busy) }, Time.GetUtcNow(), TimeSpan.FromHours(1), Ct)),
        [nameof(IKairosStore.ReleaseActivityTaskAsync)] = async (a, f) =>
            await a.Store.ReleaseActivityTaskAsync(a.ActivityTask with { Run = In(f, a.Busy) }, Time.GetUtcNow(), Time.GetUtcNow(), Ct),
        [nameof(IKairosStore.CompleteActivityTaskAsync)] = async (a, f) =>
        {
            ActivityTask hijack = a.ActivityTask with { Run = In(f, a.Busy) };
            var result = new ActivityCompleted(f.TenantId, f.NamespaceId, a.Busy.RunId, 0, Time.GetUtcNow(), a.ActivityTask.ActivityId, B8("forged"));
            Assert.Equal(CompletionOutcome.StaleLease, await a.Store.CompleteActivityTaskAsync(hijack, result, null, Time.GetUtcNow(), Ct));
        },
        [nameof(IKairosStore.ReapExpiredLeasesAsync)] = async (a, f) =>
            Assert.Equal(0, await a.Store.ReapExpiredLeasesAsync(f, Time.GetUtcNow().AddYears(1), Ct)),
        [nameof(IKairosStore.FireDueTimersAsync)] = async (a, f) =>
            Assert.Equal(0, await a.Store.FireDueTimersAsync(f, Time.GetUtcNow().AddYears(1), Ct)),
        [nameof(IKairosStore.GetStuckWorkflowTasksAsync)] = async (a, f) =>
            Assert.Empty(await a.Store.GetStuckWorkflowTasksAsync(f, Ct)),
        [nameof(IKairosStore.ListRunsAsync)] = async (a, f) =>
            Assert.Empty(await a.Store.ListRunsAsync(f, new RunFilter(), 1000, Ct)),
        [nameof(IKairosStore.GetRunGroupsAsync)] = async (a, f) =>
            Assert.Empty(await a.Store.GetRunGroupsAsync(f, null, Ct)),
        [nameof(IKairosStore.ExportHistoryAsync)] = async (a, f) =>
        {
            foreach (RunKey run in new[] { a.Busy, a.Finished, a.Parked, a.Leased })
            {
                Assert.Null(await a.Store.ExportHistoryAsync(In(f, run), "mallory", "curious", false, Time.GetUtcNow(), Ct));
            }

            Assert.Empty(await a.Store.GetAuditLogAsync(f, Ct)); // nothing was exported, so nothing is recorded in their scope either
        },
        [nameof(IKairosStore.GetBacklogAsync)] = async (a, f) =>
        {
            WorkBacklog? backlog = await a.Store.GetBacklogAsync(f, Time.GetUtcNow().AddYears(1), Ct);
            Assert.True(backlog is null or { WorkflowTasks: 0, ActivityTasks: 0, DueTimers: 0 }, "a foreign scope counted someone else's work");
        },
        [nameof(IKairosStore.ResumeWorkflowTaskAsync)] = async (a, f) =>
            Assert.False(await a.Store.ResumeWorkflowTaskAsync(In(f, a.Parked), "mallory", Time.GetUtcNow(), Ct)),
        [nameof(IKairosStore.FailRunAsync)] = async (a, f) =>
        {
            Assert.False(await a.Store.FailRunAsync(In(f, a.Busy), "sabotage", "mallory", Time.GetUtcNow(), Ct));
            Assert.False(await a.Store.FailRunAsync(In(f, a.Parked), "sabotage", "mallory", Time.GetUtcNow(), Ct));
        },
        [nameof(IKairosStore.GetAuditLogAsync)] = async (a, f) =>
            Assert.Empty(await a.Store.GetAuditLogAsync(f, Ct)),
        [nameof(IKairosStore.GetHistoryAsync)] = async (a, f) =>
        {
            foreach (RunKey run in new[] { a.Busy, a.Finished, a.Parked, a.Leased })
            {
                Assert.Empty(await a.Store.GetHistoryAsync(In(f, run), Ct));
            }
        },
        [nameof(IKairosStore.GetRunStateAsync)] = async (a, f) =>
        {
            foreach (RunKey run in new[] { a.Busy, a.Finished, a.Parked, a.Leased })
            {
                Assert.Null(await a.Store.GetRunStateAsync(In(f, run), Ct));
            }
        },
        [nameof(IKairosStore.GetCurrentRunAsync)] = async (a, f) =>
        {
            foreach (string id in a.WorkflowIds)
            {
                Assert.Null(await a.Store.GetCurrentRunAsync(f, id, Ct));
            }
        },
    };

    [Fact]
    public void Every_store_method_has_a_cross_tenant_leak_case()
    {
        string[] declared = [.. typeof(IKairosStore).GetMethods().Select(m => m.Name).Distinct()];
        Dictionary<string, Func<Arena, TenantScope, Task>> cases = LeakCases();

        Assert.True(
            !declared.Except(cases.Keys).Any(),
            $"IKairosStore methods without a cross-tenant leak case: {string.Join(", ", declared.Except(cases.Keys))}. Add them to LeakCases.");
        Assert.True(
            !cases.Keys.Except(declared).Any(),
            $"Leak cases for methods that no longer exist: {string.Join(", ", cases.Keys.Except(declared))}.");
        Assert.Equal(typeof(IKairosStore).GetMethods().Length, declared.Length); // no overloads hiding behind one name
    }

    /// <summary>A store that forgets the caller's tenant on two paths: the kind of bug these cases exist to catch.</summary>
    private sealed class ScopeBlindStore(IKairosStore inner) : ForwardingStore(inner)
    {
        public override ValueTask<IReadOnlyList<HistoryEvent>> GetHistoryAsync(RunKey run, CancellationToken cancellationToken) =>
            base.GetHistoryAsync(new RunKey(A.TenantId, A.NamespaceId, run.RunId), cancellationToken);

        public override ValueTask<bool> SignalRunAsync(RunKey run, string signalName, string signalId, byte[] payload, DateTimeOffset now, CancellationToken cancellationToken) =>
            base.SignalRunAsync(new RunKey(A.TenantId, A.NamespaceId, run.RunId), signalName, signalId, payload, now, cancellationToken);
    }

    [Fact]
    public async Task The_leak_cases_fail_against_a_store_that_ignores_the_callers_tenant()
    {
        Arena arena = await BuildArenaAsync();
        Arena blind = arena with { Store = new ScopeBlindStore(arena.Store) };

        foreach (string method in new[] { nameof(IKairosStore.GetHistoryAsync), nameof(IKairosStore.SignalRunAsync) })
        {
            await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(async () => await LeakCases()[method](blind, B));
        }
    }

    [Theory]
    [MemberData(nameof(StoreMethodNames))]
    public async Task Foreign_scopes_cannot_use_the_method_to_read_or_change_tenant_data(string method)
    {
        Dictionary<string, Func<Arena, TenantScope, Task>> cases = LeakCases();
        Assert.True(cases.TryGetValue(method, out Func<Arena, TenantScope, Task>? attack), $"No leak case for {method}.");

        Arena arena = await BuildArenaAsync();
        string before = await SnapshotAsync(arena);
        Assert.Contains("SECRET", Str(((WorkflowStarted)(await arena.Store.GetHistoryAsync(arena.Busy, Ct))[0]).Input), StringComparison.Ordinal); // the data exists

        foreach (TenantScope foreign in new[] { B, MixedNamespace, MixedTenant })
        {
            try
            {
                await attack!(arena, foreign);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                AssertNoIdentifiersLeak(arena, ex.Message); // an error is acceptable; leaking identifiers in it is not
            }

            Assert.Equal(before, await SnapshotAsync(arena));
        }

        // A's legitimate holders were not disturbed: they still own their leases and can finish.
        var done = new WorkflowCompleted(A.TenantId, A.NamespaceId, arena.Leased.RunId, 0, Time.GetUtcNow(), B8("ok"));
        Assert.Equal(CompletionOutcome.Applied, await arena.Store.CompleteWorkflowTaskAsync(arena.WorkflowTask, [done], Time.GetUtcNow(), Ct));
        var result = new ActivityCompleted(A.TenantId, A.NamespaceId, arena.Busy.RunId, 0, Time.GetUtcNow(), arena.ActivityTask.ActivityId, B8("ok"));
        Assert.Equal(CompletionOutcome.Applied, await arena.Store.CompleteActivityTaskAsync(arena.ActivityTask, result, null, Time.GetUtcNow(), Ct));
    }
}
