using System.Text;
using Kairos.Testing;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>Every region the concept pages quote is run here, against the in-memory store, with the simulator kit's virtual clock.</summary>
public sealed class ConceptExamplesTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private sealed class Harness
    {
        public InMemoryKairosStore Store { get; } = new();

        public VirtualTimeProvider Time { get; } = new();

        public List<string> Executed { get; } = [];

        public List<TaskFault> Faults { get; } = [];

        public KairosClient Client => new(Store, Time);

        public ActivityRegistry Activities() => new ActivityRegistry()
            .Register("send", Echo("send"))
            .Register("publish", Echo("publish"))
            .Register("charge", Echo("charge"))
            .Register("ship", Echo("ship"))
            .Register("fraud-check", Echo("fraud-check"))
            .Register("process-batch", Echo("process-batch"));

        public WorkerRuntime Worker(WorkflowRegistry workflows, WorkerOptions? options = null) =>
            new(Store, Scope, workflows, Activities(), Time, options ?? new WorkerOptions { AutoHeartbeat = false }) { OnFault = Faults.Add };

        private ActivityFunc Echo(string name) => (_, input, _) =>
        {
            lock (Executed)
            {
                Executed.Add(name);
            }

            return Task.FromResult(input);
        };
    }

    private static RunKey Key(StartRunResult start) => new(Scope.TenantId, Scope.NamespaceId, start.RunId);

    [Fact]
    public async Task Workflow_code_that_reads_the_ordinary_clock_or_makes_up_ids_is_caught_on_replay()
    {
        var h = new Harness();
        WorkerRuntime worker = h.Worker(new WorkflowRegistry().Register("wf", "default", DeterminismExamples.WrongAsync));
        await h.Client.StartAsync(Scope, "wrong", "wf", []);

        await Driver.DriveAsync(worker);

        Assert.NotEmpty(h.Faults);
        Assert.IsType<NonDeterminismException>(h.Faults[0].Error);
    }

    [Fact]
    public async Task Workflow_code_that_uses_the_context_replays_cleanly_and_completes()
    {
        var h = new Harness();
        WorkerRuntime worker = h.Worker(new WorkflowRegistry().Register("wf", "default", DeterminismExamples.RightAsync));
        StartRunResult start = await h.Client.StartAsync(Scope, "right", "wf", []);

        await Driver.DriveAsync(worker);

        Assert.Empty(h.Faults);
        Assert.Equal(RunState.Completed, await h.Client.GetStateAsync(Key(start)));
    }

    [Fact]
    public async Task An_approval_waits_for_the_signal_and_a_resent_decision_signals_once()
    {
        var h = new Harness();
        WorkerRuntime worker = h.Worker(new WorkflowRegistry().Register("wf", "default", SignalExamples.ApprovalAsync));
        StartRunResult start = await h.Client.StartAsync(Scope, "approval", "wf", [1]);
        RunKey run = Key(start);
        await Driver.DriveAsync(worker);
        Assert.Equal(RunState.Running, await h.Client.GetStateAsync(run));

        Assert.True(await SignalExamples.ApproveAsync(h.Client, run));
        Assert.True(await SignalExamples.ApproveAsync(h.Client, run)); // the same request sent again
        await Driver.DriveAsync(worker);

        Assert.Equal(RunState.Completed, await h.Client.GetStateAsync(run));
        Assert.Single((await h.Client.GetHistoryAsync(run)).OfType<SignalReceived>());
        Assert.Equal(["publish"], h.Executed);
    }

    [Fact]
    public async Task A_patched_change_leaves_runs_in_flight_alone_and_gives_new_runs_the_new_step()
    {
        var h = new Harness();
        WorkerRuntime v1 = h.Worker(new WorkflowRegistry().Register("order", "default", VersioningExamples.OrderV1Async));
        StartRunResult old = await h.Client.StartAsync(Scope, "old-order", "order", [1]);
        await Driver.DriveAsync(v1); // charged, now waiting for "packed"

        WorkerRuntime v2 = h.Worker(new WorkflowRegistry().Register("order", "default", VersioningExamples.OrderV2Async)); // deploy
        StartRunResult fresh = await h.Client.StartAsync(Scope, "new-order", "order", [2]);
        await Driver.DriveAsync(v2);
        await h.Client.SignalAsync(Key(old), "packed", []);
        await h.Client.SignalAsync(Key(fresh), "packed", []);
        await Driver.DriveAsync(v2);

        Assert.Empty(h.Faults);
        Assert.Equal(RunState.Completed, await h.Client.GetStateAsync(Key(old)));
        Assert.Equal(RunState.Completed, await h.Client.GetStateAsync(Key(fresh)));
        string[] oldSteps = [.. (await h.Client.GetHistoryAsync(Key(old))).OfType<ActivityScheduled>().Select(s => s.ActivityType)];
        string[] newSteps = [.. (await h.Client.GetHistoryAsync(Key(fresh))).OfType<ActivityScheduled>().Select(s => s.ActivityType)];
        Assert.Equal(["charge", "ship"], oldSteps);
        Assert.Equal(["charge", "fraud-check", "ship"], newSteps);
    }

    [Fact]
    public async Task Pinned_builds_run_side_by_side_and_each_run_finishes_on_its_own_build()
    {
        var h = new Harness();
        var options = new KairosOptions();
        options.AddOrderBuilds();
        WorkflowRegistry registry = new WorkflowRegistry()
            .Register("order", "2026-10-01", VersioningExamples.OrderV1Async)
            .Register("order", "2026-11-01", VersioningExamples.OrderV2Async);
        WorkerRuntime worker = h.Worker(registry);

        StartRunResult old = await h.Client.StartAsync(Scope, "pinned-old", "order", [1], buildId: "2026-10-01");
        StartRunResult fresh = await h.Client.StartAsync(Scope, "pinned-new", "order", [2], buildId: "2026-11-01");
        await Driver.DriveAsync(worker);
        await h.Client.SignalAsync(Key(old), "packed", []);
        await h.Client.SignalAsync(Key(fresh), "packed", []);
        await Driver.DriveAsync(worker);

        Assert.Equal(["charge", "ship"], (await h.Client.GetHistoryAsync(Key(old))).OfType<ActivityScheduled>().Select(s => s.ActivityType));
        Assert.Equal(["charge", "fraud-check", "ship"], (await h.Client.GetHistoryAsync(Key(fresh))).OfType<ActivityScheduled>().Select(s => s.ActivityType));
    }

    [Fact]
    public async Task A_long_activity_heartbeats_after_each_chunk_and_stops_when_cancelled()
    {
        int beats = 0;
        var context = new ActivityContext(new RunKey(Scope.TenantId, Scope.NamespaceId, Guid.NewGuid()), 1, "transcode", 1, "key")
        {
            Heartbeat = () =>
            {
                beats++;
                return ValueTask.CompletedTask;
            },
        };

        byte[] result = await ActivityExamples.TranscodeAsync(context, [7], CancellationToken.None);
        Assert.Equal([7], result);
        Assert.Equal(10, beats);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ActivityExamples.TranscodeAsync(context, [7], cts.Token));
    }

    [Fact]
    public async Task A_workflow_that_continues_as_new_finishes_across_several_runs_with_short_histories()
    {
        var h = new Harness();
        var options = new WorkerOptions { AutoHeartbeat = false, ContinueAsNewSuggestedAt = 40 };
        WorkerRuntime worker = h.Worker(new WorkflowRegistry().Register("batches", "default", LongRunningExamples.ProcessBatchesAsync), options);
        StartRunResult start = await h.Client.StartAsync(Scope, "batches", "batches", BitConverter.GetBytes(0));

        await Driver.DriveAsync(worker, maxSteps: 5000);

        RunKey current = (await h.Store.GetCurrentRunAsync(Scope, "batches", CancellationToken.None))!.Value;
        Assert.NotEqual(start.RunId, current.RunId);
        IReadOnlyList<HistoryEvent> last = await h.Client.GetHistoryAsync(current);
        Assert.Equal(100, BitConverter.ToInt32(((WorkflowCompleted)last[^1]).Result));
        Assert.Equal(100, h.Executed.Count(e => e == "process-batch")); // every batch ran exactly once across the hand-overs
        Assert.True(last.Count <= 60);
    }
}
