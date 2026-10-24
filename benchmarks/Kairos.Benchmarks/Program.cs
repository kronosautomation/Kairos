using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Kairos;

if (args.Length > 0 && args[0] == "groupcommit")
{
    return await GroupCommitBenchmark.RunAsync();
}

BenchmarkSwitcher.FromAssembly(typeof(ReplayBenchmarks).Assembly).Run(args);
return 0;

/// <summary>Replay of a 999-event history: a started event, then per activity a result, a task boundary and the next command.</summary>
[MemoryDiagnoser]
public class ReplayBenchmarks
{
    private const int Activities = 332;
    private static readonly byte[] s_payload = new byte[64];

    private HistoryEvent[] _history = [];
    private WorkflowFunc _workflow = null!;

    [GlobalSetup]
    public void Setup()
    {
        _workflow = static async (ctx, input) =>
        {
            for (int i = 0; i < Activities; i++)
            {
                await ctx.ExecuteActivityAsync("step", input);
            }

            return input;
        };

        var tenant = Guid.NewGuid();
        var ns = Guid.NewGuid();
        var run = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var list = new List<HistoryEvent>(Activities * 2 + 2);

        void Add(HistoryEvent e) => list.Add(HashChain.Seal(e, list.Count == 0 ? HashChain.GenesisHash : list[^1].Hash));

        long Seq() => list.Count + 1;
        void Boundary() => Add(new WorkflowTaskCompleted(tenant, ns, run, Seq(), now));

        Add(new WorkflowStarted(tenant, ns, run, 1, now, "wf", "default", s_payload));
        Boundary();
        Add(new ActivityScheduled(tenant, ns, run, Seq(), now, 1, "step", s_payload, RetryPolicy.Default));
        for (int i = 1; i <= Activities; i++)
        {
            Add(new ActivityCompleted(tenant, ns, run, Seq(), now, i, s_payload));
            Boundary();
            if (i < Activities)
            {
                Add(new ActivityScheduled(tenant, ns, run, Seq(), now, i + 1, "step", s_payload, RetryPolicy.Default));
            }
        }

        Add(new WorkflowCompleted(tenant, ns, run, Seq(), now, s_payload));
        _history = [.. list];
    }

    [Benchmark(Baseline = true)]
    public ReplayResult Replay_999_Events() => WorkflowReplayer.Replay(_workflow, _history, verifyHashChain: false);

    [Benchmark]
    public ReplayResult Replay_999_Events_VerifyHashChain() => WorkflowReplayer.Replay(_workflow, _history, verifyHashChain: true);

    [Benchmark]
    public void VerifyHashChain_999_Events() => HashChain.Verify(_history);
}

[MemoryDiagnoser]
public class HashChainBenchmarks
{
    private static readonly byte[] s_payload = new byte[256];

    private ActivityCompleted _event = null!;
    private byte[] _prev = new byte[HashChain.HashSize];

    [GlobalSetup]
    public void Setup()
    {
        _event = new ActivityCompleted(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, DateTimeOffset.UnixEpoch, 1, s_payload);
        _prev = HashChain.Seal(new WorkflowStarted(_event.TenantId, _event.NamespaceId, _event.RunId, 1, DateTimeOffset.UnixEpoch, "wf", "default", s_payload), HashChain.GenesisHash).Hash;
    }

    /// <summary>Allocates the sealed event record plus its two 32-byte hash arrays; hashing itself uses pooled buffers.</summary>
    [Benchmark]
    public HistoryEvent Seal_Append() => HashChain.Seal(_event, _prev);
}
