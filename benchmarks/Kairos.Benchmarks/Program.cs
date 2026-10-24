using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Kairos;

BenchmarkSwitcher.FromAssembly(typeof(ReplayBenchmarks).Assembly).Run(args);

/// <summary>Replay of a 1,000-event history (1 start + 499 x [scheduled, completed] + 1 completion).</summary>
[MemoryDiagnoser]
public class ReplayBenchmarks
{
    private const int Activities = 499;
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

        Add(new WorkflowStarted(tenant, ns, run, 1, now, "wf", s_payload));
        for (int i = 1; i <= Activities; i++)
        {
            Add(new ActivityScheduled(tenant, ns, run, list.Count + 1, now, i, "step", s_payload, RetryPolicy.Default));
            Add(new ActivityCompleted(tenant, ns, run, list.Count + 1, now, i, s_payload));
        }

        Add(new WorkflowCompleted(tenant, ns, run, list.Count + 1, now, s_payload));
        _history = [.. list];
    }

    [Benchmark(Baseline = true)]
    public ReplayResult Replay_1000_Events() => WorkflowReplayer.Replay(_workflow, _history, verifyHashChain: false);

    [Benchmark]
    public ReplayResult Replay_1000_Events_VerifyHashChain() => WorkflowReplayer.Replay(_workflow, _history, verifyHashChain: true);

    [Benchmark]
    public void VerifyHashChain_1000_Events() => HashChain.Verify(_history);
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
        _prev = HashChain.Seal(new WorkflowStarted(_event.TenantId, _event.NamespaceId, _event.RunId, 1, DateTimeOffset.UnixEpoch, "wf", s_payload), HashChain.GenesisHash).Hash;
    }

    /// <summary>Allocates the sealed event record plus its two 32-byte hash arrays; hashing itself uses pooled buffers.</summary>
    [Benchmark]
    public HistoryEvent Seal_Append() => HashChain.Seal(_event, _prev);
}
