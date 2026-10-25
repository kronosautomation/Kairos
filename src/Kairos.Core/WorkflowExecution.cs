using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kairos;

/// <summary>
/// One live (or replaying) workflow run.
/// <para>
/// History is applied in order. External events (start, activity results, timers, signals) only <i>resolve</i> things; workflow
/// code runs when a <see cref="WorkflowTaskCompleted"/> boundary is reached (and once more at the end of history), exactly once
/// per batch, on a private FIFO scheduler so continuations run in the order their events were applied. The commands a batch
/// produces must then match, one for one, the command events that follow the boundary, otherwise
/// <see cref="NonDeterminismException"/> is thrown. Not thread-safe.
/// </para>
/// </summary>
public sealed class WorkflowExecution : IWorkflowContext
{
    private readonly WorkflowFunc _workflow;
    private readonly Scheduler _scheduler = new();
    private readonly Queue<WorkflowCommand> _pending = new();
    private readonly Dictionary<int, PendingActivity> _activities = [];
    private readonly Dictionary<int, TaskCompletionSource> _timers = [];
    private readonly HashSet<string> _patches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SignalSlot> _signals = new(StringComparer.Ordinal);
    private Task<byte[]>? _task;
    private int _nextCommandId;
    private int _guidCounter;
    private int _historyLength;
    private bool _started;
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

    public int HistoryLength => _historyLength;

    /// <summary>History length at which <see cref="ShouldContinueAsNew"/> turns true. Defaults to <see cref="HistoryLimits.ContinueAsNewSuggestedAt"/>.</summary>
    public int ContinueAsNewSuggestedAt { get; init; } = HistoryLimits.ContinueAsNewSuggestedAt;

    public bool ShouldContinueAsNew => _historyLength >= ContinueAsNewSuggestedAt;

    public ContinueAsNewException ContinueAsNew(ReadOnlyMemory<byte> input) => new(input.ToArray());

    /// <summary>True once a WorkflowCompleted/WorkflowFailed event has been applied.</summary>
    public bool IsClosed => _closed;

    /// <summary>Commands emitted by workflow code that history has not yet confirmed.</summary>
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
        SignalSlot slot = Slot(signalName);
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

    /// <summary>Applies the next history event. A <see cref="WorkflowTaskCompleted"/> runs the batch that preceded it.</summary>
    public void Apply(HistoryEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (_closed)
        {
            throw new NonDeterminismException($"Event {e.Sequence} ({e.Type}) follows the workflow's closing event.");
        }

        _historyLength++;
        Dispatch(e);
    }

    /// <summary>
    /// Ends the history: the events since the last boundary form the batch being executed now. Leftover commands from the
    /// previous batch mean the code now does more than the recorded run did; anything produced here is genuinely new.
    /// </summary>
    public void FinishHistory()
    {
        RequireNoPendingCommands("end of history");
        RunBatch(isReplaying: false);
    }

    private void Dispatch(HistoryEvent e)
    {
        switch (e)
        {
            case WorkflowStarted started:
                if (_started)
                {
                    throw new NonDeterminismException($"Event {e.Sequence}: workflow already started.");
                }

                _started = true;
                BuildId = started.BuildId;
                Now = started.Timestamp;
                byte[] input = started.Input;
                _scheduler.Post(_ => StartWorkflow(input), null);
                break;

            case RunTerminated:
                // The store ended the run instead of recording what the last batch asked for (history cap) or an operator closed it:
                // commands that never made it into history are discarded, not a divergence.
                _pending.Clear();
                _closed = true;
                break;

            case WorkflowContinued continued:
                {
                    var command = Expect<ContinueAsNewCommand>(e);
                    if (command.NewRunId != continued.NewRunId || !command.Input.AsSpan().SequenceEqual(continued.Input))
                    {
                        throw Mismatch(e, command.NewRunId != continued.NewRunId ? "a continue-as-new into a different run" : $"a continue-as-new with a different input ({command.Input.Length} bytes against {continued.Input.Length} recorded)");
                    }

                    _closed = true;
                    break;
                }

            case WorkflowTaskCompleted:
                RequireNoPendingCommands(e);
                RunBatch(isReplaying: true);
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
                        !string.Equals(command.ActivityType, scheduled.ActivityType, StringComparison.Ordinal))
                    {
                        throw Mismatch(e, $"activity {command.ActivityId} '{command.ActivityType}'");
                    }

                    if (!command.Input.AsSpan().SequenceEqual(scheduled.Input))
                    {
                        throw Mismatch(e, $"activity {command.ActivityId} '{command.ActivityType}' with a different input ({command.Input.Length} bytes against {scheduled.Input.Length} recorded)");
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
                        throw Mismatch(e, $"a different workflow result ({command.Result.Length} bytes against {completed.Result.Length} recorded)");
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
                Now = Max(Now, e.Timestamp);
                TakeActivity(done.ActivityId, e).Completion.SetResult(done.Result);
                break;

            case ActivityFailed failure:
                if (!failure.WillRetry)
                {
                    RequireNoPendingCommands(e);
                    Now = Max(Now, e.Timestamp);
                    PendingActivity activity = TakeActivity(failure.ActivityId, e);
                    activity.Completion.SetException(new ActivityFailureException(activity.ActivityType, failure.ErrorType, failure.Message));
                }

                break;

            case TimerFired fired:
                RequireNoPendingCommands(e);
                Now = Max(Now, e.Timestamp);
                if (!_timers.Remove(fired.TimerId, out TaskCompletionSource? timerTcs))
                {
                    throw new NonDeterminismException($"Event {e.Sequence}: timer {fired.TimerId} fired but the workflow never started it.");
                }

                timerTcs.SetResult();
                break;

            case SignalReceived signal:
                RequireNoPendingCommands(e);
                Now = Max(Now, e.Timestamp);
                DeliverSignal(signal);
                break;

            default:
                throw new NonDeterminismException($"Event {e.Sequence}: unsupported event {e.GetType().Name}.");
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

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
    }

    /// <summary>Runs every continuation made ready by the events applied since the last batch, then queues a closing command if the code finished.</summary>
    private void RunBatch(bool isReplaying)
    {
        IsReplaying = isReplaying;
        _scheduler.Drain();

        if (_completionQueued || _task is not { IsCompleted: true } task)
        {
            return;
        }

        _completionQueued = true;
        if (task.IsCompletedSuccessfully)
        {
            _pending.Enqueue(new CompleteWorkflowCommand(task.Result));
        }
        else if (!task.IsCanceled && task.Exception!.GetBaseException() is ContinueAsNewException continueAsNew)
        {
            _pending.Enqueue(new ContinueAsNewCommand(NewGuid(), continueAsNew.Input));
        }
        else
        {
            Exception ex = task.IsCanceled ? new TaskCanceledException() : task.Exception!.GetBaseException();
            _pending.Enqueue(new FailWorkflowCommand(ex.GetType().Name, ex.Message));
        }
    }

    private SignalSlot Slot(string name)
    {
        if (!_signals.TryGetValue(name, out SignalSlot? slot))
        {
            slot = new SignalSlot();
            _signals.Add(name, slot);
        }

        return slot;
    }

    private void DeliverSignal(SignalReceived signal)
    {
        SignalSlot slot = Slot(signal.SignalName);
        if (slot.Waiters.TryDequeue(out TaskCompletionSource<byte[]>? waiter))
        {
            waiter.SetResult(signal.Payload);
        }
        else
        {
            slot.Buffered.Enqueue(signal.Payload);
        }
    }

    private PendingActivity TakeActivity(int activityId, HistoryEvent e) =>
        _activities.Remove(activityId, out PendingActivity? activity)
            ? activity
            : throw new NonDeterminismException($"Event {e.Sequence}: activity {activityId} resolved but the workflow never scheduled it.");

    private T Expect<T>(HistoryEvent e)
        where T : WorkflowCommand
    {
        if (!_pending.TryDequeue(out WorkflowCommand? command))
        {
            throw new NonDeterminismException(
                $"Event {e.Sequence} ({e.Type}) is in history but the replayed workflow emitted no command for it.", e.Sequence, Describe(e), "nothing");
        }

        return command as T ?? throw Mismatch(e, Describe(command));
    }

    private void RequireNoPendingCommands(HistoryEvent e) => RequireNoPendingCommands($"event {e.Sequence} ({e.Type})");

    private void RequireNoPendingCommands(string at)
    {
        if (_pending.Count > 0)
        {
            WorkflowCommand extra = _pending.Peek();
            throw new NonDeterminismException(
                $"At {at} the workflow still has an unconfirmed {extra.GetType().Name}: the code does more than the recorded run did.",
                SequenceOf(at), at.StartsWith("end of history", StringComparison.Ordinal) ? "end of history" : at, Describe(extra));
        }
    }

    private static NonDeterminismException Mismatch(HistoryEvent e, string emitted) =>
        new($"Non-determinism at event {e.Sequence}: history has {e.Type} but the workflow emitted {emitted}.", e.Sequence, Describe(e), emitted);

    private static long SequenceOf(string at)
    {
        // "event 12 (ActivityCompleted)" -> 12; "end of history" -> 0.
        const string Prefix = "event ";
        if (!at.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return 0;
        }

        int end = at.IndexOf(' ', Prefix.Length);
        return end > 0 && long.TryParse(at.AsSpan(Prefix.Length, end - Prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long n) ? n : 0;
    }

    /// <summary>A recorded event as names, ids and sizes. Never a payload.</summary>
    internal static string Describe(HistoryEvent e) => e switch
    {
        ActivityScheduled x => $"ActivityScheduled: activity {x.ActivityId} '{x.ActivityType}' ({x.Input.Length} bytes)",
        TimerStarted x => $"TimerStarted: timer {x.TimerId} for {x.Duration}",
        WorkflowCompleted x => $"WorkflowCompleted ({x.Result.Length} bytes)",
        WorkflowFailed x => $"WorkflowFailed: '{x.ErrorType}'",
        WorkflowContinued x => $"WorkflowContinued ({x.Input.Length} bytes)",
        PatchMarker x => $"PatchMarker: '{x.PatchId}'",
        ActivityCompleted x => $"ActivityCompleted: activity {x.ActivityId}",
        ActivityFailed x => $"ActivityFailed: activity {x.ActivityId} attempt {x.Attempt} '{x.ErrorType}'",
        TimerFired x => $"TimerFired: timer {x.TimerId}",
        SignalReceived x => $"SignalReceived: '{x.SignalName}'",
        _ => e.Type.ToString(),
    };

    /// <summary>A command the code emitted, as names, ids and sizes. Never a payload.</summary>
    internal static string Describe(WorkflowCommand c) => c switch
    {
        ScheduleActivityCommand x => $"ScheduleActivity: activity {x.ActivityId} '{x.ActivityType}' ({x.Input.Length} bytes)",
        StartTimerCommand x => $"StartTimer: timer {x.TimerId} for {x.Duration}",
        CompleteWorkflowCommand x => $"CompleteWorkflow ({x.Result.Length} bytes)",
        FailWorkflowCommand x => $"FailWorkflow: '{x.ErrorType}'",
        ContinueAsNewCommand x => $"ContinueAsNew ({x.Input.Length} bytes)",
        RecordPatchCommand x => $"RecordPatch: '{x.PatchId}'",
        _ => c.GetType().Name,
    };

    private sealed record PendingActivity(string ActivityType, TaskCompletionSource<byte[]> Completion);

    private sealed class SignalSlot
    {
        public Queue<byte[]> Buffered { get; } = new();

        public Queue<TaskCompletionSource<byte[]>> Waiters { get; } = new();
    }

    /// <summary>
    /// FIFO single-threaded scheduler. Workflow continuations are posted here instead of running inline when an event
    /// resolves a task, so their order depends only on the order of events in history.
    /// </summary>
    private sealed class Scheduler : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("Workflow code must not block on other threads.");

        public override SynchronizationContext CreateCopy() => this;

        public void Drain()
        {
            if (_queue.Count == 0)
            {
                return;
            }

            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try
            {
                while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) item))
                {
                    item.Callback(item.State);
                }
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
