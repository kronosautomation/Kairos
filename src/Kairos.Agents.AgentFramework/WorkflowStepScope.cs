using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;

namespace Kairos.Agents.AgentFramework;

/// <summary>An executor waited for a durable agent's answer for longer than <see cref="AgentFrameworkOptions.WorkflowAgentWait"/> allows.</summary>
public sealed class WorkflowAgentWaitException : Exception
{
    public WorkflowAgentWaitException()
        : base("An executor waited too long for a durable agent.")
    {
    }

    public WorkflowAgentWaitException(string message)
        : base(message)
    {
    }

    public WorkflowAgentWaitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>An executor of a durable workflow failed. Agent Framework reports a failing executor as events, not as an exception; the step turns them into this so that Kairos retries the superstep.</summary>
public sealed class WorkflowExecutorFailedException : Exception
{
    public WorkflowExecutorFailedException()
        : base("An executor failed.")
    {
    }

    public WorkflowExecutorFailedException(string message)
        : base(message)
    {
    }

    public WorkflowExecutorFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>No durable workflow is registered under the name, or the registered workflow is not the one the run was started with.</summary>
public sealed class UnknownWorkflowException : Exception
{
    public UnknownWorkflowException()
        : base("Unknown workflow.")
    {
    }

    public UnknownWorkflowException(string message)
        : base(message)
    {
    }

    public UnknownWorkflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// What a step of a durable workflow tells the durable agents its executors call: which step this is, so that a call repeated after a crash is recognised and answered from the conversation it started; and how long the
/// executor may wait for an answer. Set only while a step runs.
/// </summary>
internal sealed class WorkflowStepScope(string workflow, string stepKey, TimeSpan maxWait)
{
    /// <summary>The id prefix of conversations that belong to a workflow: they are cancelled with it.</summary>
    public const string OwnedPrefix = "wf.";

    private static readonly AsyncLocal<WorkflowStepScope?> Slot = new();

    private static readonly UpDownCounter<long> Waiting = Kairos.KairosMetrics.Meter.CreateUpDownCounter<long>(
        "kairos.af.workflow.conversation_waits", "{activity}", "Workflow step activities that are waiting for the answer of a durable agent's conversation");

    private readonly ConcurrentDictionary<string, int> _occurrences = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _owned = new(StringComparer.Ordinal);

    public static WorkflowStepScope? Current => Slot.Value;

    public string Workflow { get; } = workflow;

    public TimeSpan MaxWait { get; } = maxWait;

    /// <summary>The workflow ids of the conversations this step used that belong to the workflow.</summary>
    public string[] Owned => [.. _owned.Keys.Order(StringComparer.Ordinal)];

    public static IDisposable Enter(WorkflowStepScope scope)
    {
        WorkflowStepScope? previous = Slot.Value;
        Slot.Value = scope;
        return new Restore(previous);
    }

    /// <summary>
    /// A name that is the same when the step is run again: made from the step, what is being named and its content, and how many times the same thing was named before in this step (two executors that send the same
    /// text to the same agent get two names, in whatever order they ask).
    /// </summary>
    public string Name(string kind, string agent, string session, string content)
    {
        string key = $"{kind}|{agent}|{session}|{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))}";
        int n = _occurrences.AddOrUpdate(key, 1, (_, count) => count + 1);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{stepKey}|{key}|{n}")))[..32];
    }

    public void Own(string workflowId) => _owned.TryAdd(workflowId, 0);

    /// <summary>Counts an activity as waiting for a conversation until the returned scope is disposed.</summary>
    public IDisposable StartWaiting()
    {
        Waiting.Add(1, new KeyValuePair<string, object?>("workflow", Workflow));
        return new Done(Workflow);
    }

    private sealed class Restore(WorkflowStepScope? previous) : IDisposable
    {
        public void Dispose() => Slot.Value = previous;
    }

    private sealed class Done(string workflow) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Waiting.Add(-1, new KeyValuePair<string, object?>("workflow", workflow));
            }
        }
    }
}
