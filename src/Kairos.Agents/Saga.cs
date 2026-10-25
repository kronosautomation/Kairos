using System.Runtime.ExceptionServices;
using System.Text;

namespace Kairos.Agents;

/// <summary>Settings for one saga.</summary>
public sealed class SagaOptions
{
    /// <summary>
    /// How compensating activities are retried. Undoing work matters more than giving up, so the default keeps trying for hours
    /// (100 attempts, waits growing to five minutes) before it reports the compensation as failed.
    /// </summary>
    public RetryPolicy CompensationRetry { get; init; } = new()
    {
        MaximumAttempts = 100,
        InitialInterval = TimeSpan.FromSeconds(1),
        BackoffCoefficient = 2.0,
        MaximumInterval = TimeSpan.FromMinutes(5),
    };

    /// <summary>The signal that asks the saga to stop and roll back (<see cref="AgentClient.RequestCancelAsync"/> sends it).</summary>
    public string CancelSignal { get; init; } = AgentClient.CancelSignalName;
}

/// <summary>Registers undo steps while a saga runs and reports a cancel request. See <see cref="SagaExtensions.RunSagaAsync{T}"/>.</summary>
public interface ISaga
{
    /// <summary>True once someone asked the run to cancel. Checked at step boundaries; nothing in flight is abandoned.</summary>
    bool CancellationRequested { get; }

    /// <summary>The reason given with the cancel request.</summary>
    string? CancellationReason { get; }

    /// <summary>The retry policy compensating activities should use.</summary>
    RetryPolicy CompensationRetry { get; }

    /// <exception cref="SagaCancelledException">A cancel was requested. Inside the saga body this rolls the work back.</exception>
    void ThrowIfCancelled();

    /// <summary>
    /// Registers an undo step that runs one activity. Register it after the step it undoes succeeded (or before, if the step can have an
    /// effect even when it fails). The activity must be idempotent and must cope with "nothing to undo".
    /// </summary>
    void Compensate(string name, string activityType, ReadOnlyMemory<byte> input);

    /// <summary>Registers an undo step made of workflow code. Pass the given retry policy to the activities it runs.</summary>
    void Compensate(string name, Func<RetryPolicy, Task> undo);
}

/// <summary>A cancel was requested for a run inside a saga. The saga rolled back before this reached the caller.</summary>
public sealed class SagaCancelledException : Exception
{
    public SagaCancelledException()
        : base("The run was cancelled.")
    {
    }

    public SagaCancelledException(string message)
        : base(message)
    {
    }

    public SagaCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The saga failed and at least one compensation could not be completed, even after its retries. The work that was not undone is
/// listed; an operator has to finish it by hand. The original failure is the inner exception.
/// </summary>
public sealed class SagaCompensationException : Exception
{
    public SagaCompensationException(Exception original, IReadOnlyList<string> notUndone)
        : base($"The saga failed ({original?.GetType().Name}) and these compensations did not complete: {string.Join(", ", notUndone ?? [])}.", original)
    {
        NotUndone = notUndone ?? [];
    }

    public SagaCompensationException()
        : this(new InvalidOperationException(), [])
    {
    }

    public SagaCompensationException(string message)
        : base(message)
    {
        NotUndone = [];
    }

    public SagaCompensationException(string message, Exception innerException)
        : base(message, innerException)
    {
        NotUndone = [];
    }

    /// <summary>The names of the compensations that failed.</summary>
    public IReadOnlyList<string> NotUndone { get; }
}

public static class SagaExtensions
{
    /// <summary>
    /// Runs <paramref name="body"/> as a saga. If it throws (or a cancel arrives), the registered compensations run in reverse order, each
    /// as a durable step that is retried until it completes and takes effect once, and a crash during the rollback resumes it. When the
    /// rollback finishes the original exception is rethrown; if some compensation could not complete, <see cref="SagaCompensationException"/>.
    /// </summary>
    public static Task<T> RunSagaAsync<T>(this IWorkflowContext context, Func<ISaga, Task<T>> body, SagaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return SagaScope.RunAsync(context, body, options ?? new SagaOptions(), activate: null);
    }

    /// <inheritdoc cref="RunSagaAsync{T}"/>
    public static async Task RunSagaAsync(this IWorkflowContext context, Func<ISaga, Task> body, SagaOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        await RunSagaAsync(context, async saga =>
        {
            await body(saga);
            return 0;
        }, options);
    }
}

internal sealed class SagaScope : ISaga
{
    private static readonly RetryPolicy MarkRetry = RetryPolicy.Default;

    private readonly IWorkflowContext _context;
    private readonly SagaOptions _options;
    private readonly List<Step> _steps = [];
    private readonly Task<byte[]> _cancel;

    private SagaScope(IWorkflowContext context, SagaOptions options)
    {
        _context = context;
        _options = options;
        _cancel = context.WaitForSignalAsync(options.CancelSignal);
    }

    public bool CancellationRequested => _cancel.IsCompleted;

    public string? CancellationReason => _cancel.IsCompletedSuccessfully ? Encoding.UTF8.GetString(_cancel.Result) : null;

    public RetryPolicy CompensationRetry => _options.CompensationRetry;

    internal Task<byte[]> CancelTask => _cancel;

    internal int Registered => _steps.Count;

    public void ThrowIfCancelled()
    {
        if (CancellationRequested)
        {
            throw new SagaCancelledException(string.IsNullOrEmpty(CancellationReason) ? "The run was cancelled." : $"The run was cancelled: {CancellationReason}");
        }
    }

    public void Compensate(string name, string activityType, ReadOnlyMemory<byte> input)
    {
        ArgumentException.ThrowIfNullOrEmpty(activityType);
        byte[] copy = input.ToArray();
        Compensate(name, policy => _context.ExecuteActivityAsync(activityType, copy, policy));
    }

    public void Compensate(string name, Func<RetryPolicy, Task> undo)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(undo);
        _steps.Add(new Step(name, undo));
    }

    /// <param name="context">The workflow context.</param>
    /// <param name="body">The saga body.</param>
    /// <param name="options">Saga settings.</param>
    /// <param name="activate">Tells an agent context which saga is current, so its steps can notice a cancel.</param>
    internal static async Task<T> RunAsync<T>(IWorkflowContext context, Func<ISaga, Task<T>> body, SagaOptions options, Action<SagaScope?>? activate)
    {
        ArgumentNullException.ThrowIfNull(body);
        var saga = new SagaScope(context, options);
        activate?.Invoke(saga);
        Exception failure;
        try
        {
            return await body(saga);
        }
        catch (ContinueAsNewException) when (saga.Registered == 0)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex is ContinueAsNewException
                ? new InvalidOperationException("A saga cannot continue as new while it has compensations registered: they would be lost.", ex)
                : ex;
        }
        finally
        {
            activate?.Invoke(null);
        }

        await saga.RollBackAsync(failure);
        ExceptionDispatchInfo.Capture(failure).Throw();
        throw failure; // not reached
    }

    private async Task RollBackAsync(Exception cause)
    {
        if (_steps.Count == 0)
        {
            return;
        }

        string[] plan = [.. Enumerable.Reverse(_steps).Select(s => s.Name)];
        await MarkAsync(new SagaMark { Kind = SagaMarks.RollbackBegin, Names = plan });
        var failed = new List<string>();
        for (int i = _steps.Count - 1; i >= 0; i--)
        {
            Step step = _steps[i];
            await MarkAsync(new SagaMark { Kind = SagaMarks.StepBegin, Name = step.Name });
            bool ok = true;
            try
            {
                await step.Undo(_options.CompensationRetry);
            }
            catch (Exception ex) when (ex is not ContinueAsNewException)
            {
                ok = false;
                failed.Add(step.Name);
            }

            await MarkAsync(new SagaMark { Kind = SagaMarks.StepEnd, Name = step.Name, Succeeded = ok });
        }

        await MarkAsync(new SagaMark { Kind = SagaMarks.RollbackEnd, Succeeded = failed.Count == 0, Failures = [.. failed] });
        if (failed.Count > 0)
        {
            throw new SagaCompensationException(cause, failed);
        }
    }

    // The markers make a rollback readable from history (what was planned, what started, what finished). Losing one must never stop the
    // rollback itself, so a marker that cannot run is skipped.
    private async Task MarkAsync(SagaMark mark)
    {
        try
        {
            await _context.ExecuteActivityAsync(AgentActivityNames.SagaMark, WireCodec.Write(mark, AgentJson.Default.SagaMark), MarkRetry);
        }
        catch (ActivityFailureException)
        {
            // best effort
        }
    }

    private sealed record Step(string Name, Func<RetryPolicy, Task> Undo);
}

internal static class SagaMarks
{
    public const string RollbackBegin = "rollback-begin";
    public const string StepBegin = "step-begin";
    public const string StepEnd = "step-end";
    public const string RollbackEnd = "rollback-end";
}
