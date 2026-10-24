using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kairos;

/// <summary>
/// One live (or replaying) workflow run. History events are applied in order; workflow code runs inline on the caller's
/// thread, to its next suspension point, after each event. Commands the code emits must then match the command-bearing
/// events that follow in history, otherwise <see cref="NonDeterminismException"/> is thrown. Not thread-safe.
/// </summary>
public sealed class WorkflowExecution : IWorkflowContext
{
    private readonly WorkflowFunc _workflow;
    private readonly Queue<WorkflowCommand> _pending = new();
    private readonly Dictionary<int, PendingActivity> _activities = [];
    private readonly Dictionary<int, TaskCompletionSource> _timers = [];
    private readonly HashSet<string> _patches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SignalSlot> _signals = new(StringComparer.Ordinal);
    private Task<byte[]>? _task;
    private int _nextCommandId;
    private int _guidCounter;
    private bool _completionQueued;
    private bool _closed;

    public WorkflowExecution(WorkflowFunc workflow, Guid tenantId, Guid namespaceId, Guid runId)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflow = workflow;
        TenantId = tenantId;
        NamespaceId = namespaceId;
        RunId = runId;
    }

    public Guid TenantId { get; }

    public Guid NamespaceId { get; }

    public Guid RunId { get; }

    public string BuildId { get; private set; } = "";

    public DateTimeOffset Now { get; private set; }

    public bool IsReplaying { get; private set; }

    /// <summary>True once a WorkflowCompleted/WorkflowFailed event has been applied.</summary>
    public bool IsClosed => _closed;

    /// <summary>Number of commands emitted by workflow code that history has not yet confirmed.</summary>
    public int PendingCommandCount => _pending.Count;

    /// <summary>Pre-registers a patch marker found anywhere in the history about to be replayed.</summary>
    public void AddRecordedPatch(string patchId)
    {
        ArgumentException.ThrowIfNullOrEmpty(patchId);
        _patches.Add(patchId);
    }

    public bool IsPatched(string patchId)
    {
        ArgumentException.ThrowIfNullOrEmpty(patchId);
        if (_patches.Contains(patchId))
        {
            return true;
        }

        if (IsReplaying)
        {
            return false; // history predates this patch: keep the old code path
        }

        _patches.Add(patchId);
        _pending.Enqueue(new RecordPatchCommand(patchId));
        return true;
    }

    public Guid NewGuid()
    {
        Span<byte> input = stackalloc byte[20];
        RunId.TryWriteBytes(input, bigEndian: true, out _);
        BinaryPrimitives.WriteInt32LittleEndian(input[16..], ++_guidCounter);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }

    public Task DelayAsync(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        int id = ++_nextCommandId;
        _pending.Enqueue(new StartTimerCommand(id, duration));
        var tcs = new TaskCompletionSource();
        _timers.Add(id, tcs);
        return tcs.Task;
    }

    public Task<byte[]> ExecuteActivityAsync(string activityType, ReadOnlyMemory<byte> input, RetryPolicy? retryPolicy = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(activityType);
        RetryPolicy policy = retryPolicy ?? RetryPolicy.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.MaximumAttempts, 1, nameof(retryPolicy));
        int id = ++_nextCommandId;
        _pending.Enqueue(new ScheduleActivityCommand(id, activityType, input.ToArray(), policy));
        var tcs = new TaskCompletionSource<byte[]>();
        _activities.Add(id, new PendingActivity(activityType, tcs));
        return tcs.Task;
    }

    public Task<byte[]> WaitForSignalAsync(string signalName)
    {
        ArgumentException.ThrowIfNullOrEmpty(signalName);
        if (!_signals.TryGetValue(signalName, out SignalSlot? slot))
        {
            slot = new SignalSlot();
            _signals.Add(signalName, slot);
        }

        if (slot.Buffered.TryDequeue(out byte[]? payload))
        {
            return Task.FromResult(payload);
        }

        var tcs = new TaskCompletionSource<byte[]>();
        slot.Waiters.Enqueue(tcs);
        return tcs.Task;
    }

    /// <summary>Moves all commands emitted since the last drain into <paramref name="destination"/>.</summary>
    public void DrainCommands(ICollection<WorkflowCommand> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        while (_pending.TryDequeue(out WorkflowCommand? command))
        {
            destination.Add(command);
        }
    }

    /// <summary>Applies one history event, then runs workflow code until it next suspends.</summary>
    /// <param name="e">The next event, in sequence order.</param>
    /// <param name="isReplaying">False only for the final event of the history being processed.</param>
    public void Apply(HistoryEvent e, bool isReplaying)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_closed)
        {
            throw new NonDeterminismException($"Event {e.Sequence} ({e.Type}) follows the workflow's closing event.");
        }

        // Continuations must run inline on this thread; a captured SynchronizationContext would defer them.
        SynchronizationContext? previous = SynchronizationContext.Current;
        if (previous is not null)
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        try
        {
            Now = e.Timestamp;
            IsReplaying = isReplaying;
            Dispatch(e);
        }
        finally
        {
            if (previous is not null)
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }

    private void Dispatch(HistoryEvent e)
    {
        switch (e)
        {
            case WorkflowStarted started:
                if (_task is not null)
                {
                    throw new NonDeterminismException($"Event {e.Sequence}: workflow already started.");
                }

                BuildId = started.BuildId;
                StartWorkflow(started.Input);
                break;

            case PatchMarker marker:
                _patches.Add(marker.PatchId);
                if (_pending.TryPeek(out WorkflowCommand? head) && head is RecordPatchCommand rp &&
                    string.Equals(rp.PatchId, marker.PatchId, StringComparison.Ordinal))
                {
                    _pending.Dequeue();
                }

                break;

            case ActivityScheduled scheduled:
                {
                    var command = Expect<ScheduleActivityCommand>(e);
                    if (command.ActivityId != scheduled.ActivityId ||
                        !string.Equals(command.ActivityType, scheduled.ActivityType, StringComparison.Ordinal) ||
                        !command.Input.AsSpan().SequenceEqual(scheduled.Input))
                    {
                        throw Mismatch(e, $"activity {command.ActivityId} '{command.ActivityType}'");
                    }

                    break;
                }

            case TimerStarted timer:
                {
                    var command = Expect<StartTimerCommand>(e);
                    if (command.TimerId != timer.TimerId || command.Duration != timer.Duration)
                    {
                        throw Mismatch(e, $"timer {command.TimerId} for {command.Duration}");
                    }

                    break;
                }

            case WorkflowCompleted completed:
                {
                    var command = Expect<CompleteWorkflowCommand>(e);
                    if (!command.Result.AsSpan().SequenceEqual(completed.Result))
                    {
                        throw Mismatch(e, "a different workflow result");
                    }

                    _closed = true;
                    break;
                }

            case WorkflowFailed failed:
                {
                    var command = Expect<FailWorkflowCommand>(e);
                    if (!string.Equals(command.ErrorType, failed.ErrorType, StringComparison.Ordinal))
                    {
                        throw Mismatch(e, $"failure '{command.ErrorType}'");
                    }

                    _closed = true;
                    break;
                }

            case ActivityCompleted done:
                RequireNoPendingCommands(e);
                TakeActivity(done.ActivityId, e).tcs.SetResult(done.Result);
                AfterResolution();
                break;

            case ActivityFailed failure:
                if (!failure.WillRetry)
                {
                    RequireNoPendingCommands(e);
                    (string activityType, TaskCompletionSource<byte[]> tcs) = TakeActivity(failure.ActivityId, e);
                    tcs.SetException(new ActivityFailureException(activityType, failure.ErrorType, failure.Message));
                    AfterResolution();
                }

                break;

            case TimerFired fired:
                RequireNoPendingCommands(e);
                if (!_timers.Remove(fired.TimerId, out TaskCompletionSource? timerTcs))
                {
                    throw new NonDeterminismException($"Event {e.Sequence}: timer {fired.TimerId} fired but the workflow never started it.");
                }

                timerTcs.SetResult();
                AfterResolution();
                break;

            case SignalReceived signal:
                RequireNoPendingCommands(e);
                DeliverSignal(signal);
                AfterResolution();
                break;

            default:
                throw new NonDeterminismException($"Event {e.Sequence}: unsupported event {e.GetType().Name}.");
        }
    }

    private void StartWorkflow(byte[] input)
    {
        try
        {
            _task = _workflow(this, input);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _task = Task.FromException<byte[]>(ex);
        }

        AfterResolution();
    }

    /// <summary>Called after workflow code has run: if it finished, queue the closing command exactly once.</summary>
    private void AfterResolution()
    {
        if (_completionQueued || _task is not { IsCompleted: true } task)
        {
            return;
        }

        _completionQueued = true;
        if (task.IsCompletedSuccessfully)
        {
            _pending.Enqueue(new CompleteWorkflowCommand(task.Result));
        }
        else
        {
            Exception ex = task.IsCanceled ? new TaskCanceledException() : task.Exception!.GetBaseException();
            _pending.Enqueue(new FailWorkflowCommand(ex.GetType().Name, ex.Message));
        }
    }

    private void DeliverSignal(SignalReceived signal)
    {
        if (!_signals.TryGetValue(signal.SignalName, out SignalSlot? slot))
        {
            slot = new SignalSlot();
            _signals.Add(signal.SignalName, slot);
        }

        if (slot.Waiters.TryDequeue(out TaskCompletionSource<byte[]>? waiter))
        {
            waiter.SetResult(signal.Payload);
        }
        else
        {
            slot.Buffered.Enqueue(signal.Payload);
        }
    }

    private (string tcsType, TaskCompletionSource<byte[]> tcs) TakeActivity(int activityId, HistoryEvent e)
    {
        if (!_activities.Remove(activityId, out PendingActivity? activity))
        {
            throw new NonDeterminismException($"Event {e.Sequence}: activity {activityId} resolved but the workflow never scheduled it.");
        }

        return (activity.ActivityType, activity.Completion);
    }

    private T Expect<T>(HistoryEvent e)
        where T : WorkflowCommand
    {
        if (!_pending.TryDequeue(out WorkflowCommand? command))
        {
            throw new NonDeterminismException(
                $"Event {e.Sequence} ({e.Type}) is in history but the replayed workflow emitted no command for it.");
        }

        return command as T ?? throw Mismatch(e, command.GetType().Name);
    }

    private void RequireNoPendingCommands(HistoryEvent e)
    {
        if (_pending.Count > 0)
        {
            throw new NonDeterminismException(
                $"Event {e.Sequence} ({e.Type}) arrived while the workflow's {_pending.Peek().GetType().Name} has no matching history event.");
        }
    }

    private static NonDeterminismException Mismatch(HistoryEvent e, string emitted) =>
        new($"Non-determinism at event {e.Sequence}: history has {e.Type} but the workflow emitted {emitted}.");

    private sealed record PendingActivity(string ActivityType, TaskCompletionSource<byte[]> Completion);

    private sealed class SignalSlot
    {
        public Queue<byte[]> Buffered { get; } = new();

        public Queue<TaskCompletionSource<byte[]>> Waiters { get; } = new();
    }
}
