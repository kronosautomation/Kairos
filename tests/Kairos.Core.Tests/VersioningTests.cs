using System.Text;
using Kairos.Testing;

namespace Kairos.Core.Tests;

/// <summary>Invariant I10: in-flight runs keep running correctly across deploys (build pinning + patch API).</summary>
public sealed class VersioningTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private readonly VirtualTimeProvider _time = new();
    private readonly InMemoryKairosStore _store = new();
    private readonly List<string> _executed = [];

    private KairosClient Client => new(_store, _time);

    private ActivityRegistry Activities() => new ActivityRegistry()
        .Register("a", (_, _, _) => Record("a"))
        .Register("b", (_, _, _) => Record("b"))
        .Register("c", (_, _, _) => Record("c"));

    private Task<byte[]> Record(string name)
    {
        _executed.Add(name);
        return Task.FromResult(Encoding.UTF8.GetBytes(name));
    }

    private WorkerRuntime Worker(WorkflowRegistry wf, WorkerOptions? options = null) =>
        new(_store, Scope, wf, Activities(), _time, options ?? new WorkerOptions { FailedTaskRetryDelay = TimeSpan.FromSeconds(1) });

    private static readonly WorkflowFunc V1 = async (ctx, _) =>
    {
        await ctx.ExecuteActivityAsync("a", ReadOnlyMemory<byte>.Empty);
        await ctx.WaitForSignalAsync("go");
        await ctx.ExecuteActivityAsync("c", ReadOnlyMemory<byte>.Empty);
        return [];
    };

    private static readonly WorkflowFunc V2Patched = async (ctx, _) =>
    {
        await ctx.ExecuteActivityAsync("a", ReadOnlyMemory<byte>.Empty);
        if (ctx.IsPatched("add-b"))
        {
            await ctx.ExecuteActivityAsync("b", ReadOnlyMemory<byte>.Empty);
        }

        await ctx.WaitForSignalAsync("go");
        await ctx.ExecuteActivityAsync("c", ReadOnlyMemory<byte>.Empty);
        return [];
    };

    private static readonly WorkflowFunc V2Unpatched = async (ctx, _) =>
    {
        await ctx.ExecuteActivityAsync("a", ReadOnlyMemory<byte>.Empty);
        await ctx.ExecuteActivityAsync("b", ReadOnlyMemory<byte>.Empty); // inserted without a patch gate
        await ctx.WaitForSignalAsync("go");
        await ctx.ExecuteActivityAsync("c", ReadOnlyMemory<byte>.Empty);
        return [];
    };

    private async Task<RunKey> StartInFlightOnV1()
    {
        StartRunResult start = await Client.StartAsync(Scope, "inflight", "wf", [], cancellationToken: CancellationToken.None);
        var key = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        await Driver.DriveAsync(Worker(new WorkflowRegistry().Register("wf", "default", V1)));
        Assert.Equal(RunState.Running, await Client.GetStateAsync(key));
        Assert.Equal(["a"], _executed);
        return key;
    }

    [Fact]
    public async Task Patched_change_leaves_in_flight_runs_on_the_old_path_and_new_runs_on_the_new_path()
    {
        RunKey inFlight = await StartInFlightOnV1();
        WorkerRuntime v2 = Worker(new WorkflowRegistry().Register("wf", "default", V2Patched)); // deploy: same build id, patched code

        // New run takes the patched branch and records a marker.
        StartRunResult fresh = await Client.StartAsync(Scope, "fresh", "wf", []);
        var freshKey = new RunKey(Scope.TenantId, Scope.NamespaceId, fresh.RunId);
        _executed.Clear();
        await Driver.DriveAsync(v2);
        Assert.Contains("b", _executed);
        Assert.Single((await Client.GetHistoryAsync(freshKey)).OfType<PatchMarker>());

        // The in-flight run replays old history on the old branch (no b) and finishes.
        _executed.Clear();
        await Client.SignalAsync(inFlight, "go", []);
        await Client.SignalAsync(freshKey, "go", []);
        await Driver.DriveAsync(v2);

        Assert.Equal(RunState.Completed, await Client.GetStateAsync(inFlight));
        Assert.Equal(RunState.Completed, await Client.GetStateAsync(freshKey));
        Assert.Empty((await Client.GetHistoryAsync(inFlight)).OfType<PatchMarker>());
        Assert.DoesNotContain(
            (await Client.GetHistoryAsync(inFlight)).OfType<ActivityScheduled>(), s => s.ActivityType == "b");
        HashChain.Verify(await Client.GetHistoryAsync(inFlight));
        HashChain.Verify(await Client.GetHistoryAsync(freshKey));
    }

    [Fact]
    public async Task Unpatched_change_to_in_flight_run_is_detected_not_silently_applied()
    {
        RunKey inFlight = await StartInFlightOnV1();
        var faults = new List<TaskFault>();
        WorkerRuntime bad = Worker(new WorkflowRegistry().Register("wf", "default", V2Unpatched));
        bad.OnFault = faults.Add;

        await Client.SignalAsync(inFlight, "go", []);
        await Driver.DriveAsync(bad);

        Assert.NotEmpty(faults);
        Assert.IsType<NonDeterminismException>(faults[0].Error);
        Assert.Equal(RunState.Running, await Client.GetStateAsync(inFlight)); // not advanced, not failed, not lost
        Assert.DoesNotContain("b", _executed);

        // Rolling back to the old code lets the very same run finish.
        _time.Advance(TimeSpan.FromSeconds(2));
        await Driver.DriveAsync(Worker(new WorkflowRegistry().Register("wf", "default", V1)));
        Assert.Equal(RunState.Completed, await Client.GetStateAsync(inFlight));
    }

    [Fact]
    public async Task Pinned_runs_finish_on_their_original_build_while_new_runs_use_the_new_build()
    {
        // Two builds with different (incompatible) shapes are deployed side by side.
        WorkflowFunc v2Incompatible = async (ctx, _) =>
        {
            await ctx.ExecuteActivityAsync("b", ReadOnlyMemory<byte>.Empty);
            return [];
        };

        StartRunResult old = await Client.StartAsync(Scope, "old", "wf", [], buildId: "v1");
        var oldKey = new RunKey(Scope.TenantId, Scope.NamespaceId, old.RunId);
        await Driver.DriveAsync(Worker(new WorkflowRegistry().Register("wf", "v1", V1))); // reaches the signal wait

        StartRunResult fresh = await Client.StartAsync(Scope, "new", "wf", [], buildId: "v2");
        var freshKey = new RunKey(Scope.TenantId, Scope.NamespaceId, fresh.RunId);
        WorkerRuntime both = Worker(new WorkflowRegistry().Register("wf", "v1", V1).Register("wf", "v2", v2Incompatible));
        await Client.SignalAsync(oldKey, "go", []);
        await Driver.DriveAsync(both);

        Assert.Equal(RunState.Completed, await Client.GetStateAsync(oldKey));
        Assert.Equal(RunState.Completed, await Client.GetStateAsync(freshKey));
        Assert.Equal(["a", "c"], (await Client.GetHistoryAsync(oldKey)).OfType<ActivityScheduled>().Select(s => s.ActivityType));
        Assert.Equal(["b"], (await Client.GetHistoryAsync(freshKey)).OfType<ActivityScheduled>().Select(s => s.ActivityType));
    }

    [Fact]
    public async Task A_worker_without_the_runs_build_leaves_it_untouched()
    {
        StartRunResult old = await Client.StartAsync(Scope, "old", "wf", [], buildId: "v1");
        var key = new RunKey(Scope.TenantId, Scope.NamespaceId, old.RunId);

        await Driver.DriveAsync(Worker(new WorkflowRegistry().Register("wf", "v2", V2Patched)));

        Assert.Single(await Client.GetHistoryAsync(key)); // only WorkflowStarted: nothing ran, nothing was lost
        await Driver.DriveAsync(Worker(new WorkflowRegistry().Register("wf", "v1", V1)));
        Assert.Equal(2, (await Client.GetHistoryAsync(key)).OfType<ActivityScheduled>().Count() + 1);
    }

    [Fact]
    public async Task Repeated_checks_of_one_patch_record_a_single_marker_and_replay_identically()
    {
        WorkflowFunc twice = async (ctx, _) =>
        {
            bool first = ctx.IsPatched("p");
            await ctx.ExecuteActivityAsync("a", ReadOnlyMemory<byte>.Empty);
            bool second = ctx.IsPatched("p");
            return [first && second ? (byte)1 : (byte)0];
        };
        StartRunResult start = await Client.StartAsync(Scope, "twice", "wf", []);
        var key = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        await Driver.DriveAsync(Worker(new WorkflowRegistry().Register("wf", "default", twice)));

        IReadOnlyList<HistoryEvent> history = await Client.GetHistoryAsync(key);
        Assert.Single(history.OfType<PatchMarker>());
        Assert.Equal([1], ((WorkflowCompleted)history[^1]).Result);

        ReplayResult replay = WorkflowReplayer.Replay(twice, history);
        Assert.True(replay.IsClosed);
        Assert.Empty(replay.NewCommands);
    }
}
