namespace Kairos;

/// <summary>
/// The only sanctioned way for workflow code to observe time, randomness, or the outside world.
/// Every member is deterministic under replay. Payloads are opaque bytes (encrypted by the Worker SDK).
/// </summary>
public interface IWorkflowContext
{
    Guid TenantId { get; }

    Guid NamespaceId { get; }

    Guid RunId { get; }

    /// <summary>The build this run is pinned to (set when the run started).</summary>
    string BuildId { get; }

    /// <summary>Workflow time: the timestamp of the history event being processed. Never the wall clock.</summary>
    DateTimeOffset Now { get; }

    /// <summary>True while replaying events that already exist in history.</summary>
    bool IsReplaying { get; }

    /// <summary>
    /// Safe-change gate for in-flight runs. Returns true for runs that reach this point for the first time under new code
    /// (and records a marker so replays agree), and false when replaying history written before the patch existed.
    /// </summary>
    bool IsPatched(string patchId);

    /// <summary>Number of history events applied to this run so far. Deterministic: identical on every replay.</summary>
    int HistoryLength { get; }

    /// <summary>True once the history is long enough that the workflow should hand over to a fresh run (<see cref="HistoryLimits"/>).</summary>
    bool ShouldContinueAsNew { get; }

    /// <summary>Builds the exception that ends this run and starts a new one: <c>throw ctx.ContinueAsNew(input);</c></summary>
    ContinueAsNewException ContinueAsNew(ReadOnlyMemory<byte> input);

    /// <summary>A deterministic GUID derived from the run id and a per-run counter.</summary>
    Guid NewGuid();

    /// <summary>Durable timer; completes after <paramref name="duration"/> of workflow time.</summary>
    Task DelayAsync(TimeSpan duration);

    /// <summary>Schedules an activity. A null <paramref name="retryPolicy"/> means <see cref="RetryPolicy.Default"/>.</summary>
    /// <exception cref="ActivityFailureException">The activity failed terminally.</exception>
    Task<byte[]> ExecuteActivityAsync(string activityType, ReadOnlyMemory<byte> input, RetryPolicy? retryPolicy = null);

    /// <summary>Completes with the next payload delivered for <paramref name="signalName"/> (FIFO per name).</summary>
    Task<byte[]> WaitForSignalAsync(string signalName);
}
