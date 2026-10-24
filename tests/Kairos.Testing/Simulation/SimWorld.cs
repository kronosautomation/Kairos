using System.Security.Cryptography;

namespace Kairos.Testing.Simulation;

public sealed class SimOptions
{
    public int MaxSteps { get; init; } = 300;

    public int MinRuns { get; init; } = 3;

    public int MaxRuns { get; init; } = 8;

    public int Workers { get; init; } = 3;

    public bool InjectFaults { get; init; } = true;

    /// <summary>Ship the patched build mid-simulation, with runs in flight.</summary>
    public bool Deploy { get; init; } = true;

    /// <summary>Share of activities that run for about three leases and must heartbeat to survive (L1).</summary>
    public double SlowActivityProbability { get; init; } = 0.1;

    /// <summary>Share of activities that kill their worker on every attempt (L2).</summary>
    public double PoisonActivityProbability { get; init; } = 0.05;

    /// <summary>Slow activities renew their lease. Turn off only to show what happens without it.</summary>
    public bool HeartbeatSlowActivities { get; init; } = true;

    /// <summary>Time may also pass while a slow activity runs, from other workers' steps (long pauses). Off for strict no-expiry scenarios.</summary>
    public bool ClockJumpsDuringSlowActivities { get; init; } = true;

    public SimFaultOptions Faults { get; init; } = new();
}

public sealed record SimResult(ulong Seed, int Runs, SimStats Stats, string Fingerprint, IReadOnlyList<byte[]?> Results);

/// <summary>
/// The whole engine in one process with every source of non-determinism drawn from one seed: virtual clock, seeded RNG,
/// simulated store faults, message delay/duplication, worker crashes, clock jumps and a mid-flight deploy.
/// After the chaos phase, faults stop and the world is driven to quiescence, then invariants I1-I4, I9 are checked.
/// A failure message always carries the seed; rerunning that seed reproduces it bit for bit.
/// </summary>
public sealed class SimWorld
{
    private static readonly byte[] SignalPayload = [7];

    private readonly SimOptions _options;
    private readonly SimRandom _rng;
    private readonly SimStats _stats = new();
    private readonly VirtualTimeProvider _time = new();
    private readonly IKairosStore _inner;
    private readonly IStoreDiagnostics _diag;
    private readonly FaultInjectingStore _store;
    private readonly KairosClient _client;
    private readonly FakeExternalSystem _external = new();
    private readonly TenantScope _scope;
    private readonly List<RunInfo> _runs = [];
    private List<WorkerRuntime> _workers = [];
    private bool _patched;

    public SimWorld(
        ulong seed, SimOptions? options = null, IReadOnlyList<SimProgram>? programs = null, Func<IKairosStore, IKairosStore>? decorate = null,
        StoreUnderTest? backend = null)
    {
        backend ??= StoreUnderTest.InMemory();
        _inner = backend.Store;
        _diag = backend.Diagnostics;
        _options = options ?? new SimOptions();
        Seed = seed;
        _rng = new SimRandom(seed);
        _store = new FaultInjectingStore(decorate?.Invoke(_inner) ?? _inner, _rng.Fork(1), _options.Faults, _stats) { FaultsEnabled = _options.InjectFaults };
        SimRandom ids = _rng.Fork(2);
        _client = new KairosClient(_store, _time, ids.NextGuid);
        _scope = new TenantScope(_rng.NextGuid(), _rng.NextGuid());

        int count = programs?.Count ?? _rng.Between(_options.MinRuns, _options.MaxRuns);
        for (int i = 0; i < count; i++)
        {
            SimProgram program = programs is null
                ? SimPrograms.Generate(_rng, $"p{i}", _options.SlowActivityProbability, _options.PoisonActivityProbability)
                : programs[i];
            _runs.Add(new RunInfo(i, program));
        }

        BuildWorkers();
    }

    public ulong Seed { get; }

    public FaultInjectingStore Store => _store;

    public static async Task<SimResult> RunSeedAsync(
        ulong seed, SimOptions? options = null, Func<IKairosStore, IKairosStore>? decorate = null, StoreUnderTest? backend = null)
    {
        var world = new SimWorld(seed, options, decorate: decorate, backend: backend);
        try
        {
            await world.ChaosAsync();
            await world.QuiesceAsync();
            return await world.CheckInvariantsAsync();
        }
        catch (InvariantViolationException ex)
        {
            throw new InvariantViolationException(ex.Invariant, $"seed={seed}: {ex.Message[(ex.Message.IndexOf(']') + 2)..]}");
        }
    }

    /// <summary>Faultless drive of the given programs; used for crash-at-every-step (set <see cref="Store"/>.ForcedFault first).</summary>
    public async Task<SimResult> RunToCompletionAsync()
    {
        _store.FaultsEnabled = false;
        await QuiesceAsync();
        return await CheckInvariantsAsync();
    }

    // ---- chaos phase ------------------------------------------------------------------------------------------

    private async Task ChaosAsync()
    {
        for (int step = 0; step < _options.MaxSteps; step++)
        {
            _stats.Steps++;
            try
            {
                await StepAsync();
            }
            catch (Exception ex) when (ex is SimulatedFaultException or OutOfMemoryException)
            {
                BuildWorkers(); // the process died; a fresh one takes over (workers keep no state between tasks)
            }
        }
    }

    private async Task StepAsync(bool allowClockJumps = true)
    {
        int roll = _rng.Next(100);
        if (roll < 28)
        {
            await PickWorker().ProcessWorkflowTaskAsync(CancellationToken.None);
        }
        else if (roll < 52)
        {
            await PickWorker().ProcessActivityTaskAsync(CancellationToken.None);
        }
        else if (roll < 62)
        {
            await PickWorker().FireTimersAsync(CancellationToken.None);
        }
        else if (roll < 72)
        {
            if (allowClockJumps)
            {
                _time.Advance(TimeSpan.FromMilliseconds(_rng.Between(1, 5000)));
            }
        }
        else if (roll < 77)
        {
            if (!allowClockJumps)
            {
                return;
            }

            _stats.ClockJumps++;
            _time.Advance(TimeSpan.FromMinutes(_rng.Between(1, 120)));
        }
        else if (roll < 83)
        {
            await _store.DeliverOneDelayedAsync();
        }
        else if (roll < 90)
        {
            await TrySendSignalAsync(_rng);
        }
        else if (roll < 98)
        {
            await TryStartAsync(_rng);
        }
        else if (_options.Deploy && !_patched)
        {
            _patched = true;
            BuildWorkers();
        }
    }

    private int _slowDepth;

    private async Task<byte[]> SimActivity(ActivityContext ctx, byte[] input, CancellationToken ct)
    {
        // A nested slow activity would advance the shared clock while its host cannot heartbeat (one thread simulates the cluster).
        bool slow = input.Length > 2 && input[2] != 0 && _slowDepth == 0;
        bool poison = input.Length > 3 && input[3] != 0;
        if (poison)
        {
#pragma warning disable CA2201 // The point: this is what a dying process looks like to the worker runtime.
            throw new OutOfMemoryException("simulated poison task: the worker dies on every attempt");
#pragma warning restore CA2201
        }

        if (slow)
        {
            TimeSpan lease = TimeSpan.FromSeconds(30);
            for (int i = 0; i < 3; i++)
            {
                _time.Advance(lease * 0.9);
                if (_options.HeartbeatSlowActivities)
                {
                    await ctx.HeartbeatAsync();
                }

                ct.ThrowIfCancellationRequested(); // the lease was lost: stop, someone else owns the attempt
                if (_slowDepth < 2)
                {
                    _slowDepth++;
                    try
                    {
                        // While this activity is "running", the rest of the cluster carries on around it.
                        for (int k = 0; k < 3; k++)
                        {
                            try
                            {
                                await StepAsync(_options.ClockJumpsDuringSlowActivities);
                            }
                            catch (Exception ex) when (ex is SimulatedFaultException or OutOfMemoryException)
                            {
                                // a different worker died; it is not our problem
                            }
                        }
                    }
                    finally
                    {
                        _slowDepth--;
                    }
                }

                ct.ThrowIfCancellationRequested();
            }
        }

        return await _external.Activity()(ctx, input, ct);
    }

    private WorkerRuntime PickWorker() => _workers[_rng.Next(_workers.Count)];

    private void BuildWorkers()
    {
        var workflows = new WorkflowRegistry();
        foreach (RunInfo r in _runs)
        {
            workflows.Register(r.Program.Type, KairosClient.DefaultBuildId, SimPrograms.Compile(r.Program, _patched));
        }

        var activities = new ActivityRegistry().Register("act", SimActivity);
        var options = new WorkerOptions
        {
            WorkflowTaskLease = TimeSpan.FromSeconds(30),
            ActivityTaskLease = TimeSpan.FromSeconds(30),
            FailedTaskRetryDelay = TimeSpan.FromSeconds(10),
            AutoHeartbeat = false, // virtual time has no timers; slow activities heartbeat explicitly
        };

        _workers = [];
        for (int i = 0; i < _options.Workers; i++)
        {
            _workers.Add(new WorkerRuntime(_store, _scope, workflows, activities, _time, options)
            {
                OnFault = _ => _stats.NonDeterminismFaults++,
            });
        }
    }

    private async Task TryStartAsync(SimRandom rng)
    {
        RunInfo? next = _runs.Find(r => r.Key is null);
        if (next is null)
        {
            return;
        }

        StartRunResult result = await _client.StartAsync(_scope, $"wf-{next.Index}", next.Program.Type, [(byte)next.Index]);
        next.Key = new RunKey(_scope.TenantId, _scope.NamespaceId, result.RunId);

        if (rng.Chance(0.15))
        {
            StartRunResult again = await _client.StartAsync(_scope, $"wf-{next.Index}", next.Program.Type, [(byte)next.Index]);
            if (again.Started || again.RunId != result.RunId)
            {
                throw new InvariantViolationException("I2", $"Re-starting workflow id wf-{next.Index} created a second run.");
            }
        }
    }

    private async Task TrySendSignalAsync(SimRandom rng)
    {
        var pending = new List<(RunInfo Run, string Name)>();
        foreach (RunInfo r in _runs)
        {
            if (r.Key is null)
            {
                continue;
            }

            foreach (string name in r.Program.SignalNames)
            {
                if (!r.SignalsSent.Contains(name))
                {
                    pending.Add((r, name));
                }
            }
        }

        if (pending.Count == 0)
        {
            return;
        }

        (RunInfo run, string signal) = pending[rng.Next(pending.Count)];
        await _client.SignalAsync(run.Key!.Value, signal, SignalPayload);
        run.SignalsSent.Add(signal); // an After-fault throws above, so it is re-sent later (a harmless duplicate)
    }

    // ---- quiescence -------------------------------------------------------------------------------------------

    private async Task QuiesceAsync()
    {
        _store.FaultsEnabled = false;
        for (int iteration = 0; iteration < 800; iteration++)
        {
            try
            {
                while (_runs.Exists(r => r.Key is null))
                {
                    await TryStartAsync(_rng);
                }

                while (await TryAnySignalAsync())
                {
                }

                await _store.DeliverAllDelayedAsync();
                foreach (WorkerRuntime w in _workers)
                {
                    await Driver.DriveAsync(w);
                }
            }
            catch (Exception ex) when (ex is SimulatedFaultException or OutOfMemoryException)
            {
                BuildWorkers(); // a crash (forced fault, or a poison task killing its worker): recover and carry on
                continue;
            }

            if ((await _diag.GetStatsAsync(_scope)).OpenRuns == 0 && _store.DelayedCount == 0)
            {
                return;
            }

            if (await _diag.NextWakeupAsync(_scope, _time.GetUtcNow()) is { } wake)
            {
                _time.AdvanceTo(wake);
            }
            else
            {
                _time.Advance(TimeSpan.FromMinutes(10));
            }
        }
    }

    private async Task<bool> TryAnySignalAsync()
    {
        foreach (RunInfo r in _runs)
        {
            foreach (string name in r.Program.SignalNames)
            {
                if (!r.SignalsSent.Contains(name))
                {
                    await _client.SignalAsync(r.Key!.Value, name, SignalPayload);
                    r.SignalsSent.Add(name);
                    return true;
                }
            }
        }

        return false;
    }

    // ---- invariants -------------------------------------------------------------------------------------------

    private async Task<SimResult> CheckInvariantsAsync()
    {
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<byte[]?>();
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (RunInfo r in _runs)
        {
            IReadOnlyList<HistoryEvent> history = await _inner.GetHistoryAsync(r.Key!.Value, CancellationToken.None);
            string where = $"run {r.Index} {r.Program}";

            // I1: nothing silently stalls.
            if (history[^1] is not (WorkflowCompleted or WorkflowFailed))
            {
                string tail = string.Join(", ", history.TakeLast(5).Select(e => $"{e.Sequence}:{e.Type}"));
                throw new InvariantViolationException("I1", $"{where} never closed. Tail: {tail}");
            }

            // I2 + I6: gap-free sequence, valid chain, sane event structure.
            try
            {
                HashChain.Verify(history);
            }
            catch (HistoryIntegrityException ex)
            {
                throw new InvariantViolationException("I2", $"{where}: {ex.Message}");
            }

            CheckStructure(history, where, expectedKeys);
            _stats.LeaseExpirations += history.OfType<ActivityFailed>().Count(f => f.ErrorType == StoreRules.LeaseExpiredError);

            // I4: the history replays with the current code, no divergence, nothing left to do.
            WorkflowFunc code = SimPrograms.Compile(r.Program, _patched);
            try
            {
                ReplayResult full = WorkflowReplayer.Replay(code, history, verifyHashChain: false);
                if (!full.IsClosed || full.NewCommands.Count != 0)
                {
                    throw new InvariantViolationException("I4", $"{where}: replay of the closed history was not a no-op ({full.NewCommands.Count} new commands).");
                }

                int prefixes = Math.Min(history.Count, 80);
                for (int i = 1; i <= prefixes; i++)
                {
                    if (IsCommitPoint(history, i))
                    {
                        WorkflowReplayer.Replay(code, history.Take(i).ToArray(), verifyHashChain: false);
                    }
                }
            }
            catch (NonDeterminismException ex)
            {
                throw new InvariantViolationException("I4", $"{where}: {ex.Message}");
            }

            // I9: bounded history.
            if (history.Count > 30 + (20 * (r.Program.Steps.Count + 1)))
            {
                throw new InvariantViolationException("I9", $"{where}: history grew to {history.Count} events.");
            }

            results.Add(history[^1] is WorkflowCompleted c ? c.Result : null);
            fingerprint.AppendData(history[^1].Hash);
        }

        // I3: at-most-once effect per key; only scheduled activities have keys; every completed activity took effect.
        foreach (string key in _external.Calls.Keys)
        {
            if (!expectedKeys.Contains(key))
            {
                throw new InvariantViolationException("I3", $"External system saw idempotency key {key} that no history scheduled.");
            }
        }

        foreach (RunInfo r in _runs)
        {
            foreach (ActivityCompleted done in (await _inner.GetHistoryAsync(r.Key!.Value, CancellationToken.None)).OfType<ActivityCompleted>())
            {
                string key = $"{done.RunId:N}:{done.ActivityId}";
                if (!_external.Effects.Contains(key))
                {
                    throw new InvariantViolationException("I3", $"Activity {key} completed but its external effect never happened.");
                }
            }
        }

        // I9 + I1: no leaked work.
        StoreStats s = await _diag.GetStatsAsync(_scope);
        if (s.WorkflowTasks != 0 || s.ActivityTasks != 0 || s.Timers != 0 || s.OpenRuns != 0 || s.StuckWorkflowTasks != 0)
        {
            throw new InvariantViolationException("I9", $"Store still holds work after all runs closed: {s}.");
        }

        if (_stats.NonDeterminismFaults != 0)
        {
            throw new InvariantViolationException("I4", $"{_stats.NonDeterminismFaults} workflow tasks failed replay with non-determinism.");
        }

        return new SimResult(Seed, _runs.Count, _stats, Convert.ToHexString(fingerprint.GetHashAndReset()), results);
    }

    private static void CheckStructure(IReadOnlyList<HistoryEvent> history, string where, HashSet<string> expectedKeys)
    {
        var scheduled = new HashSet<int>();
        var maxAttempts = new Dictionary<int, int>();
        var terminal = new HashSet<int>();
        var lastAttempt = new Dictionary<int, int>();
        var firedTimers = new HashSet<int>();
        var startedTimers = new HashSet<int>();

        if (history[0] is not WorkflowStarted)
        {
            throw new InvariantViolationException("I2", $"{where}: history does not begin with WorkflowStarted.");
        }

        for (int i = 0; i < history.Count; i++)
        {
            HistoryEvent e = history[i];
            bool isLast = i == history.Count - 1;
            bool ok = e switch
            {
                WorkflowStarted => i == 0,
                WorkflowCompleted or WorkflowFailed => isLast,
                ActivityScheduled s => scheduled.Add(s.ActivityId) && expectedKeys.Add($"{s.RunId:N}:{s.ActivityId}") &&
                    maxAttempts.TryAdd(s.ActivityId, s.RetryPolicy.MaximumAttempts),
                ActivityCompleted c => scheduled.Contains(c.ActivityId) && terminal.Add(c.ActivityId),
                ActivityFailed f => scheduled.Contains(f.ActivityId) && !terminal.Contains(f.ActivityId) &&
                    Advance(lastAttempt, f.ActivityId, f.Attempt) && f.Attempt <= maxAttempts[f.ActivityId] && (f.WillRetry || terminal.Add(f.ActivityId)),
                TimerStarted t => startedTimers.Add(t.TimerId),
                TimerFired t => startedTimers.Contains(t.TimerId) && firedTimers.Add(t.TimerId),
                _ => true,
            };

            if (!ok)
            {
                throw new InvariantViolationException("I2", $"{where}: unexpected or duplicated {e.Type} at sequence {e.Sequence}.");
            }
        }
    }

    /// <summary>
    /// A store commits a task's boundary and command events atomically, so a history never ends between them. Prefixes that do
    /// are not states the system can be in and are skipped.
    /// </summary>
    private static bool IsCommitPoint(IReadOnlyList<HistoryEvent> history, int length)
    {
        if (length >= history.Count)
        {
            return true;
        }

        static bool IsCommandSide(HistoryEvent e) =>
            e.Type is HistoryEventType.WorkflowTaskCompleted or HistoryEventType.ActivityScheduled or HistoryEventType.TimerStarted
                or HistoryEventType.PatchMarker;

        return !(IsCommandSide(history[length - 1]) && (IsCommandSide(history[length]) ||
            history[length].Type is HistoryEventType.WorkflowCompleted or HistoryEventType.WorkflowFailed));
    }

    private static bool Advance(Dictionary<int, int> lastAttempt, int activityId, int attempt)
    {
        int previous = lastAttempt.GetValueOrDefault(activityId);
        lastAttempt[activityId] = attempt;
        return attempt == previous + 1;
    }

    private sealed class RunInfo(int index, SimProgram program)
    {
        public int Index { get; } = index;

        public SimProgram Program { get; } = program;

        public RunKey? Key { get; set; }

        public HashSet<string> SignalsSent { get; } = new(StringComparer.Ordinal);
    }
}
