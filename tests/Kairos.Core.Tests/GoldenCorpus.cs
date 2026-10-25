using System.Text;
using Kairos.Testing;
using Kairos.Testing.Simulation;

namespace Kairos.Core.Tests;

/// <summary>One recorded history and how today's code must replay it.</summary>
internal sealed record CorpusEntry(string File, string ReplayWith, string Note, IReadOnlyList<HistoryEvent> History);

/// <summary>
/// The golden history corpus (docs/TEST-STRATEGY.md layer 6). Histories recorded by a released build are stored in the repo and
/// every later build must replay them without non-determinism (I4), or an intentional change must be gated by the patch API (I10).
/// Files are text (.khist), one event per line as base64 of its canonical body and its two hashes, so changes are reviewable.
/// </summary>
internal static class GoldenCorpus
{
    public const string RecordedBy = "v1";

    private static readonly Dictionary<string, SimProgram> SimCases = new()
    {
        ["single-activity"] = new("p0", [Act()], -1),
        ["retry-after-effect"] = new("p0", [Act(fail: 2, afterEffect: true)], -1),
        ["exhausted-uncaught"] = new("p0", [Act(fail: 3, max: 2)], -1),
        ["exhausted-caught-then-more"] = new("p0", [Act(fail: 3, max: 2, catches: true), Act()], -1),
        ["timer-then-act"] = new("p0", [new TimerStep(90), Act()], -1),
        ["signal-then-act"] = new("p0", [new SignalStep("sig0"), Act()], -1),
        ["parallel"] = new("p0", [new ParStep(Act(), Act(fail: 1, afterEffect: true))], -1),
        ["poison-caught-then-more"] = new("p0", [new ActStep(0, false, 2, true, Poison: true), Act()], -1),
        ["long-activity"] = new("p0", [new ActStep(0, false, 3, false, Slow: true), Act()], -1),
        ["mixed"] = new("p0", [Act(), new TimerStep(30), new SignalStep("sig2"), new ParStep(Act(), Act()), Act()], -1),
        ["patch-point"] = new("p0", [Act(), new SignalStep("sig1"), Act()], 1),
    };

    private static ActStep Act(int fail = 0, bool afterEffect = false, int max = 4, bool catches = false) => new(fail, afterEffect, max, catches);

    /// <summary>Resolves a <c>replay-with</c> key to the workflow code today's build must replay a history with.</summary>
    public static WorkflowFunc Resolve(string key)
    {
        string[] parts = key.Split(':');
        return parts[0] switch
        {
            "sim" => SimPrograms.Compile(SimCases[parts[1]], patchedBuild: parts.Length > 2 && parts[2] == "patched"),
            "summer" => ReferenceWorkflows.Summer(int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)),
            "waits-forever" => ReferenceWorkflows.WaitsForever(),
            "runaway" => ReferenceWorkflows.Runaway(),
            _ => throw new ArgumentException($"Unknown corpus workflow '{key}'."),
        };
    }

    // ---- file format ----------------------------------------------------------------------------------------------

    public static string Serialize(CorpusEntry entry) => HistoryFile.Serialize(entry.History, RecordedBy, entry.Note, entry.ReplayWith);

    public static CorpusEntry Parse(string file, string text)
    {
        HistoryFileContents contents = HistoryFile.Parse(text);
        return new CorpusEntry(file, contents.ReplayWith, contents.Note, contents.History);
    }

    public static string Outcome(HistoryEvent last) => HistoryFile.Outcome(last);

    // ---- recording (run once per release; never re-record to make a failing replay pass) --------------------------------

    public static async Task<List<CorpusEntry>> RecordAllAsync()
    {
        var entries = new List<CorpusEntry>();

        foreach ((string name, SimProgram program) in SimCases)
        {
            var options = new SimOptions { InjectFaults = false, Deploy = false, Workers = 1 };
            var world = new SimWorld(11, options, [program]);
            await world.RunToCompletionAsync();
            IReadOnlyList<HistoryEvent> history = (await world.GetHistoriesAsync())[0];
            entries.Add(new CorpusEntry($"sim-{name}.khist", $"sim:{name}", $"Simulated workflow '{name}' written by the unpatched build.", history));
        }

        // The deploy simulation: the run above (patch-point) has no patch marker; today's code ships the patch and must still replay it.
        entries.Add(entries.Single(e => e.File == "sim-patch-point.khist") with
        {
            File = "sim-patch-point-replayed-by-patched-build.khist",
            ReplayWith = "sim:patch-point:patched",
            Note = "History written before the patch existed, replayed by the build that ships the patch (I10).",
        });
        {
            var options = new SimOptions { InjectFaults = false, Deploy = false, Workers = 1, StartPatched = true };
            var world = new SimWorld(11, options, [SimCases["patch-point"]]);
            await world.RunToCompletionAsync();
            entries.Add(new CorpusEntry(
                "sim-patch-point-written-by-patched-build.khist", "sim:patch-point:patched", "A run started by the patched build: records the patch marker.",
                (await world.GetHistoriesAsync())[0]));
        }

        // Continue-as-new chain.
        {
            var store = new InMemoryKairosStore();
            var time = new VirtualTimeProvider();
            int ids = 0;
            var client = new KairosClient(store, time, () => new Guid(++ids, 0, 0, [0, 0, 0, 0, 0, 0, 0, 7]));
            var scope = new TenantScope(new Guid(1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), new Guid(2, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]));
            WorkflowRegistry wf = new WorkflowRegistry().Register("wf", KairosClient.DefaultBuildId, ReferenceWorkflows.Summer(16));
            ActivityRegistry act = new ActivityRegistry().Register("echo", (_, i, _) => Task.FromResult(i));
            StartRunResult start = await client.StartAsync(scope, "summer", "wf", ReferenceWorkflows.SumInput(1, 12));
            await Driver.DriveAsync(new WorkerRuntime(store, scope, wf, act, time));

            var key = new RunKey(scope.TenantId, scope.NamespaceId, start.RunId);
            int n = 0;
            while (true)
            {
                IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(key);
                entries.Add(new CorpusEntry($"continue-as-new-run-{n++}.khist", "summer:16", "One run of a continue-as-new chain (sum of 1..12, hand-over at 16 events).", history));
                if (history[^1] is not WorkflowContinued next)
                {
                    break;
                }

                key = new RunKey(scope.TenantId, scope.NamespaceId, next.NewRunId);
            }
        }

        // Operator termination and the history cap.
        {
            var store = new InMemoryKairosStore(new StoreLimits { MaxHistoryEvents = 20 });
            var time = new VirtualTimeProvider();
            int ids = 100;
            var client = new KairosClient(store, time, () => new Guid(++ids, 0, 0, [0, 0, 0, 0, 0, 0, 0, 9]));
            var scope = new TenantScope(new Guid(3, 0, 0, [0, 0, 0, 0, 0, 0, 0, 3]), new Guid(4, 0, 0, [0, 0, 0, 0, 0, 0, 0, 4]));
            WorkflowRegistry wf = new WorkflowRegistry()
                .Register("waits", KairosClient.DefaultBuildId, ReferenceWorkflows.WaitsForever())
                .Register("runaway", KairosClient.DefaultBuildId, ReferenceWorkflows.Runaway());
            ActivityRegistry act = new ActivityRegistry().Register("echo", (_, i, _) => Task.FromResult(i));
            var worker = new WorkerRuntime(store, scope, wf, act, time);

            StartRunResult waiting = await client.StartAsync(scope, "waiting", "waits", []);
            StartRunResult runaway = await client.StartAsync(scope, "runaway", "runaway", []);
            await Driver.DriveAsync(worker);
            var waitingKey = new RunKey(scope.TenantId, scope.NamespaceId, waiting.RunId);
            await store.FailRunAsync(waitingKey, "operator: abandoned after the incident", "ops", time.GetUtcNow(), CancellationToken.None);

            entries.Add(new CorpusEntry("operator-terminated.khist", "waits-forever", "A run closed by an operator (RunTerminated).", await client.GetHistoryAsync(waitingKey)));
            entries.Add(new CorpusEntry(
                "history-cap-terminated.khist", "runaway", "A runaway run stopped by the history cap.",
                await client.GetHistoryAsync(new RunKey(scope.TenantId, scope.NamespaceId, runaway.RunId))));
        }

        return entries;
    }
}
