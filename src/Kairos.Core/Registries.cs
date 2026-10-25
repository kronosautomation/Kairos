namespace Kairos;

/// <summary>Everything an activity attempt may know about itself. <see cref="IdempotencyKey"/> is stable across retries.</summary>
public sealed record ActivityContext(RunKey Run, int ActivityId, string ActivityType, int Attempt, string IdempotencyKey)
{
    /// <summary>Set by the worker. Renews the attempt lease; if the lease was lost, the activity's cancellation token is cancelled.</summary>
    public Func<ValueTask>? Heartbeat { get; init; }

    /// <summary>The usage an activity reported with <see cref="ReportUsage"/>; the worker records it in the completion event.</summary>
    public UsageRecord? Usage { get; private set; }

    /// <summary>
    /// Reports what this attempt consumed (a model step reports its tokens). It is recorded once, in the event that completes the activity, so a replay never counts it again.
    /// Attempts that fail are not recorded.
    /// </summary>
    public void ReportUsage(UsageRecord usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        Usage = usage.Model.Length <= UsageRecord.MaxModelLength ? usage : usage with { Model = usage.Model[..UsageRecord.MaxModelLength] };
    }

    /// <summary>Long-running activities call this regularly (the worker also heartbeats automatically while an activity runs).</summary>
    public ValueTask HeartbeatAsync() => Heartbeat is null ? ValueTask.CompletedTask : Heartbeat();
}

public delegate Task<byte[]> ActivityFunc(ActivityContext context, byte[] input, CancellationToken cancellationToken);

/// <summary>Workflow code keyed by (type, build). Old builds stay registered so pinned runs finish on the code they started with.</summary>
public sealed class WorkflowRegistry
{
    private readonly Dictionary<(string Type, string Build), WorkflowFunc> _workflows = [];
    private readonly HashSet<string> _builds = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> BuildIds => _builds;

    public WorkflowRegistry Register(string workflowType, string buildId, WorkflowFunc workflow)
    {
        ArgumentException.ThrowIfNullOrEmpty(workflowType);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentNullException.ThrowIfNull(workflow);
        _workflows[(workflowType, buildId)] = workflow;
        _builds.Add(buildId);
        return this;
    }

    public bool TryGet(string workflowType, string buildId, out WorkflowFunc workflow)
    {
        bool found = _workflows.TryGetValue((workflowType, buildId), out WorkflowFunc? f);
        workflow = f!;
        return found;
    }
}

public sealed class ActivityRegistry
{
    private readonly Dictionary<string, ActivityFunc> _activities = new(StringComparer.Ordinal);

    public ActivityRegistry Register(string activityType, ActivityFunc activity)
    {
        ArgumentException.ThrowIfNullOrEmpty(activityType);
        ArgumentNullException.ThrowIfNull(activity);
        _activities[activityType] = activity;
        return this;
    }

    public bool TryGet(string activityType, out ActivityFunc activity)
    {
        bool found = _activities.TryGetValue(activityType, out ActivityFunc? f);
        activity = f!;
        return found;
    }
}
