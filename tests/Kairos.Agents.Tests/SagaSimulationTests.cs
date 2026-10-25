using Kairos.Testing;
using Kairos.Testing.Simulation;

namespace Kairos.Agents.Tests;

/// <summary>
/// Layer 2 for sagas. Random saga programs (steps that may fail transiently or for good, approvals a person may grant or refuse, cancel
/// requests, compensations that fail on their first attempts) run in one process under a seeded clock and a store that loses, delays and
/// duplicates messages. Afterwards invariants I1-I4, I6 and I11 are checked. Every failure names its seed; reproduce it with
/// <c>KAIROS_SIM_START_SEED=&lt;seed&gt; KAIROS_SIM_SEEDS=1 dotnet test --filter Category=Simulation</c>.
/// </summary>
public sealed class SagaSimulationTests
{
    private enum Kind
    {
        Do,
        Approve,
        Fail,
    }

    private sealed record Step(Kind Kind, int FailAttempts, bool AfterEffect, int UndoFailAttempts);

    private sealed record Program(IReadOnlyList<Step> Steps)
    {
        public override string ToString() => string.Join(',', Steps.Select((s, i) => $"{i}:{s.Kind}{(s.Kind == Kind.Do ? $"(f{s.FailAttempts}{(s.AfterEffect ? "e" : "")},u{s.UndoFailAttempts})" : "")}"));
    }

    private static ulong EnvU64(string name, ulong fallback) =>
        ulong.TryParse(Environment.GetEnvironmentVariable(name), out ulong v) ? v : fallback;

    private static Program Generate(SimRandom rng)
    {
        int count = rng.Between(1, 6);
        var steps = new List<Step>(count);
        for (int i = 0; i < count; i++)
        {
            int roll = rng.Next(100);
            steps.Add(roll switch
            {
                < 70 => new Step(Kind.Do, rng.Chance(0.75) ? 0 : rng.Between(1, 5), rng.Chance(0.5), rng.Chance(0.6) ? 0 : rng.Between(1, 6)),
                < 88 => new Step(Kind.Approve, 0, false, 0),
                _ => new Step(Kind.Fail, 0, false, 0),
            });
        }

        return new Program(steps);
    }

    private static readonly RetryPolicy StepRetry = new() { MaximumAttempts = 4, InitialInterval = TimeSpan.FromSeconds(1), MaximumInterval = TimeSpan.FromSeconds(8) };

    private static Func<IAgentContext, byte[], Task<byte[]>> Compile(Program program) => async (ctx, _) =>
    {
        await ctx.RunSagaAsync(async saga =>
        {
            for (int i = 0; i < program.Steps.Count; i++)
            {
                Step step = program.Steps[i];
                int index = i;
                switch (step.Kind)
                {
                    case Kind.Do:
                        saga.ThrowIfCancelled();
                        await ctx.Workflow.ExecuteActivityAsync("do", new byte[] { (byte)i, (byte)step.FailAttempts, step.AfterEffect ? (byte)1 : (byte)0 }, StepRetry);
                        saga.Compensate($"s{i}", rp => ctx.Workflow.ExecuteActivityAsync("undo", new byte[] { (byte)index, (byte)step.UndoFailAttempts }, rp));
                        break;
                    case Kind.Approve:
                        (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = $"approve {i}", Requirement = new ApprovalRequirement(Roles: ["r"]) })).ThrowIfNotApproved();
                        break;
                    default:
                        throw new InvalidOperationException("planned failure");
                }
            }
        });
        return [1];
    };

    private sealed class World
    {
        private static readonly ApproverIdentity Approver = new("user:sim", "Sim", ["r"]);

        private readonly SimRandom _rng;
        private readonly AgentHarness _h;
        private readonly List<Program> _programs = [];
        private readonly List<RunKey?> _runs = [];
        private readonly HashSet<(int Run, string Approval)> _decided = [];

        public World(ulong seed)
        {
            Seed = seed;
            _rng = new SimRandom(seed);
            SimRandom gen = _rng.Fork(7);
            _h = new AgentHarness(b => b.AddModel("m", AgentHarness.Echo()), faultInjection: true, seed: seed);
            _h.Faults!.FaultsEnabled = true;

            int count = gen.Between(2, 6);
            for (int i = 0; i < count; i++)
            {
                Program p = Generate(gen);
                _programs.Add(p);
                _runs.Add(null);
                _h.Workflow($"p{i}", Compile(p));
            }

            _h.Activity("do", (ctx, input, _) =>
            {
                bool shouldFail = ctx.Attempt <= input[1];
                if (shouldFail && input[2] == 0)
                {
                    throw new InvalidOperationException("transient failure before effect");
                }

                _h.External.Apply(ctx.IdempotencyKey);
                if (shouldFail)
                {
                    throw new InvalidOperationException("transient failure after effect");
                }

                return Task.FromResult(new[] { (byte)1 });
            });
            _h.Activity("undo", (ctx, input, _) =>
            {
                if (ctx.Attempt <= input[1])
                {
                    throw new InvalidOperationException("compensation target is down");
                }

                _h.External.Apply(ctx.IdempotencyKey);
                return Task.FromResult(new[] { (byte)1 });
            });
        }

        public ulong Seed { get; }

        public SimStats Stats => _h.Stats;

        public int RollbackCount { get; private set; }

        public async Task RunAsync()
        {
            for (int step = 0; step < 400; step++)
            {
                try
                {
                    await StepAsync();
                }
                catch (SimulatedFaultException)
                {
                    _h.Stats.NonDeterminismFaults += 0;
                    NewWorker();
                }
            }

            await QuiesceAsync();
            await CheckAsync();
        }

        private WorkerRuntime _worker = null!;

        private void NewWorker() => _worker = _h.NewWorker();

        private async Task StepAsync()
        {
            _worker ??= _h.NewWorker();
            int roll = _rng.Next(100);
            if (roll < 30)
            {
                await _worker.ProcessWorkflowTaskAsync(CancellationToken.None);
            }
            else if (roll < 55)
            {
                await _worker.ProcessActivityTaskAsync(CancellationToken.None);
            }
            else if (roll < 65)
            {
                await _worker.FireTimersAsync(CancellationToken.None);
            }
            else if (roll < 75)
            {
                _h.Time.Advance(TimeSpan.FromMilliseconds(_rng.Between(1, 5000)));
            }
            else if (roll < 79)
            {
                _h.Stats.ClockJumps++;
                _h.Time.Advance(TimeSpan.FromMinutes(_rng.Between(1, 120)));
            }
            else if (roll < 85)
            {
                await _h.Faults!.DeliverOneDelayedAsync();
            }
            else if (roll < 93)
            {
                await ActorAsync(allowRefusal: true);
            }
            else
            {
                await StartNextAsync();
            }
        }

        private async Task StartNextAsync()
        {
            int next = _runs.FindIndex(r => r is null);
            if (next < 0)
            {
                return;
            }

            StartRunResult s = await _h.Client.StartAsync(AgentHarness.Scope, $"w{next}", $"p{next}", []);
            _runs[next] = new RunKey(AgentHarness.Scope.TenantId, AgentHarness.Scope.NamespaceId, s.RunId);
        }

        /// <summary>The people and callers around the engine: approve, refuse, or ask a run to cancel.</summary>
        private async Task ActorAsync(bool allowRefusal)
        {
            int i = _rng.Next(_runs.Count);
            if (_runs[i] is not { } run)
            {
                return;
            }

            if (allowRefusal && _rng.Chance(0.12))
            {
                await _h.Agent.RequestCancelAsync(run, "sim cancel");
                return;
            }

            foreach (PendingApproval pending in AgentHistory.PendingApprovals(await _h.HistoryAsync(run)))
            {
                if (_decided.Contains((i, pending.ApprovalId)))
                {
                    continue;
                }

                bool approve = !allowRefusal || _rng.Chance(0.7);
                bool sent = approve
                    ? await _h.Agent.ApproveAsync(run, pending.ApprovalId, Approver)
                    : await _h.Agent.RejectAsync(run, pending.ApprovalId, Approver);
                if (sent)
                {
                    _decided.Add((i, pending.ApprovalId));
                }

                return;
            }
        }

        private async Task QuiesceAsync()
        {
            _h.Faults!.FaultsEnabled = false;
            for (int iteration = 0; iteration < 1500; iteration++)
            {
                try
                {
                    while (_runs.Exists(r => r is null))
                    {
                        await StartNextAsync();
                    }

                    for (int i = 0; i < _runs.Count; i++)
                    {
                        foreach (PendingApproval pending in AgentHistory.PendingApprovals(await _h.HistoryAsync(_runs[i]!.Value)))
                        {
                            if (_decided.Add((i, pending.ApprovalId)))
                            {
                                await _h.Agent.ApproveAsync(_runs[i]!.Value, pending.ApprovalId, Approver);
                            }
                        }
                    }

                    await _h.Faults.DeliverAllDelayedAsync();
                    _worker ??= _h.NewWorker();
                    await Driver.DriveAsync(_worker);
                }
                catch (SimulatedFaultException)
                {
                    NewWorker();
                    continue;
                }

                if (_h.MemoryStore.GetStats(AgentHarness.Scope).OpenRuns == 0 && _h.Faults.DelayedCount == 0)
                {
                    return;
                }

                if (_h.MemoryStore.NextWakeup(AgentHarness.Scope, _h.Time.GetUtcNow()) is { } wake)
                {
                    _h.Time.AdvanceTo(wake);
                }
                else
                {
                    _h.Time.Advance(TimeSpan.FromMinutes(10));
                }
            }
        }

        private async Task CheckAsync()
        {
            for (int i = 0; i < _runs.Count; i++)
            {
                RunKey run = _runs[i]!.Value;
                IReadOnlyList<HistoryEvent> history = await _h.HistoryAsync(run);
                string where = $"run {i} [{_programs[i]}]";

                if (history[^1] is not (WorkflowCompleted or WorkflowFailed))
                {
                    throw new InvariantViolationException("I1", $"{where} never closed. Tail: {string.Join(", ", history.TakeLast(5).Select(e => $"{e.Sequence}:{e.Type}"))}");
                }

                try
                {
                    HashChain.Verify(history); // I2, I6
                }
                catch (HistoryIntegrityException ex)
                {
                    throw new InvariantViolationException("I2", $"{where}: {ex.Message}");
                }

                // I4: the closed history replays to nothing new with the same code.
                try
                {
                    ReplayResult replay = WorkflowReplayer.Replay(_h.Runtime.Workflow(Compile(_programs[i])), history, verifyHashChain: false);
                    if (!replay.IsClosed || replay.NewCommands.Count != 0)
                    {
                        throw new InvariantViolationException("I4", $"{where}: replay of the closed history produced {replay.NewCommands.Count} new commands.");
                    }
                }
                catch (NonDeterminismException ex)
                {
                    throw new InvariantViolationException("I4", $"{where}: {ex.Message}");
                }

                // I11: a rollback that began completed, in order.
                if (SagaHistory.CheckCompensationsComplete(history) is { } violation)
                {
                    throw new InvariantViolationException("I11", $"{where}: {violation}");
                }

                CheckCompensationEffects(history, run, where);
                RollbackCount += SagaHistory.Rollbacks(history).Count;
            }

            if (Stats.NonDeterminismFaults != 0)
            {
                throw new InvariantViolationException("I4", $"{Stats.NonDeterminismFaults} workflow tasks failed replay.");
            }
        }

        private void CheckCompensationEffects(IReadOnlyList<HistoryEvent> history, RunKey run, string where)
        {
            var scheduled = history.OfType<ActivityScheduled>().ToList();
            string KeyOf(ActivityScheduled a) => $"{run.RunId:N}:{a.ActivityId}";
            HashSet<int> completedIds = [.. history.OfType<ActivityCompleted>().Select(c => c.ActivityId)];

            List<ActivityScheduled> dos = scheduled.Where(a => a.ActivityType == "do").ToList();
            List<ActivityScheduled> undos = scheduled.Where(a => a.ActivityType == "undo").ToList();
            List<int> doneIndexes = [.. dos.Where(a => completedIds.Contains(a.ActivityId)).Select(a => (int)a.Input[0])];
            bool failed = history[^1] is WorkflowFailed;

            // Compensations exist exactly when the run failed, for exactly the steps that completed, in reverse order, once each.
            if (!failed)
            {
                if (undos.Count != 0)
                {
                    throw new InvariantViolationException("I11", $"{where}: the run completed but scheduled {undos.Count} compensations.");
                }
            }
            else
            {
                IEnumerable<int> expected = Enumerable.Reverse(doneIndexes);
                int[] actual = [.. undos.Select(a => (int)a.Input[0])];
                if (!expected.SequenceEqual(actual))
                {
                    throw new InvariantViolationException("I11", $"{where}: completed steps [{string.Join(',', doneIndexes)}] but compensations ran for [{string.Join(',', actual)}].");
                }

                foreach (ActivityScheduled undo in undos)
                {
                    if (!completedIds.Contains(undo.ActivityId) || !_h.External.Effects.Contains(KeyOf(undo)))
                    {
                        throw new InvariantViolationException("I11", $"{where}: compensation {undo.ActivityId} did not complete with its effect.");
                    }
                }
            }

            // I3: no effect for a key that no history scheduled; every completed forward step took effect.
            HashSet<string> keys = [.. dos.Select(KeyOf), .. undos.Select(KeyOf)];
            foreach (string key in _h.External.Effects.Where(k => k.StartsWith(run.RunId.ToString("N"), StringComparison.Ordinal)))
            {
                if (!keys.Contains(key))
                {
                    throw new InvariantViolationException("I3", $"{where}: effect for key {key} that no history scheduled.");
                }
            }

            foreach (ActivityScheduled d in dos.Where(a => completedIds.Contains(a.ActivityId)))
            {
                if (!_h.External.Effects.Contains(KeyOf(d)))
                {
                    throw new InvariantViolationException("I3", $"{where}: step {d.ActivityId} completed but its effect never happened.");
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Simulation")]
    public async Task Seeded_chaos_holds_invariants_for_sagas_I1_I2_I3_I4_I6_and_I11()
    {
        ulong start = EnvU64("KAIROS_SIM_START_SEED", 1);
        ulong count = EnvU64("KAIROS_SIM_SEEDS", 300);
        ulong minutes = EnvU64("KAIROS_SIM_DURATION_MINUTES", 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var failures = new List<string>();
        var total = new SimStats();
        int rollbacks = 0;

        for (ulong seed = start; count == 0 || seed < start + count; seed++)
        {
            if (minutes > 0 && clock.Elapsed.TotalMinutes >= minutes)
            {
                break;
            }

            var world = new World(seed);
            try
            {
                await world.RunAsync();
                total.Add(world.Stats);
                rollbacks += world.RollbackCount;
            }
            catch (InvariantViolationException ex)
            {
                failures.Add($"seed={seed}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failing seeds:\n{string.Join('\n', failures.Take(10))}");

        // A green run that never rolled anything back, or never crashed, proves nothing.
        Assert.True(rollbacks > 20, $"Only {rollbacks} rollbacks happened across the seeds.");
        Assert.True(total.Crashes > 0 && total.DuplicateMessages > 0 && total.DelayedMessages > 0, $"The simulation injected too little: {total}");
    }
}
