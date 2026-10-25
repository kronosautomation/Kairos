using System.Text.Json;

namespace Kairos.Agents.A2aClient;

/// <summary>Settings for one A2A call.</summary>
public sealed class A2aCallOptions
{
    /// <summary>Retry policy for the step that sends the message. Null: three attempts.</summary>
    public RetryPolicy? Retry { get; init; }

    /// <summary>Continue an existing task, for example to answer a question the agent asked (<see cref="A2aInputRequiredException"/>).</summary>
    public string? TaskId { get; init; }

    /// <summary>Continue an existing conversation.</summary>
    public string? ContextId { get; init; }

    /// <summary>A structured part sent with the text, as a JSON object.</summary>
    public string? DataJson { get; init; }

    /// <summary>Workflow time after which a task that has not finished is cancelled remotely and the call fails with <see cref="TimeoutException"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan MinimumPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaximumPollInterval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>What the remote agent produced.</summary>
/// <param name="Text">Text of the reply message or of the task's artifacts.</param>
/// <param name="DataJson">The first structured part, as JSON.</param>
/// <param name="TaskId">The remote task id, when the agent used a task.</param>
/// <param name="ContextId">The conversation id, to continue the conversation.</param>
public sealed record A2aAgentResult(string? Text, string? DataJson, string? TaskId, string? ContextId);

public class A2aAgentException : Exception
{
    public A2aAgentException()
        : base("The A2A call failed.")
    {
    }

    public A2aAgentException(string message)
        : base(message)
    {
    }

    public A2aAgentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string? TaskId { get; init; }

    public string? ContextId { get; init; }
}

/// <summary>The agent's task failed.</summary>
public sealed class A2aAgentFailedException : A2aAgentException
{
    public A2aAgentFailedException()
    {
    }

    public A2aAgentFailedException(string message)
        : base(message)
    {
    }

    public A2aAgentFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The agent declined to do the work.</summary>
public sealed class A2aAgentRejectedException : A2aAgentException
{
    public A2aAgentRejectedException()
    {
    }

    public A2aAgentRejectedException(string message)
        : base(message)
    {
    }

    public A2aAgentRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The agent's task was cancelled by someone else.</summary>
public sealed class A2aAgentCancelledException : A2aAgentException
{
    public A2aAgentCancelledException()
    {
    }

    public A2aAgentCancelledException(string message)
        : base(message)
    {
    }

    public A2aAgentCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The agent asked a question (input-required). Answer it with another call that sets <see cref="A2aCallOptions.TaskId"/>.</summary>
public sealed class A2aInputRequiredException : A2aAgentException
{
    public A2aInputRequiredException()
    {
    }

    public A2aInputRequiredException(string message)
        : base(message)
    {
    }

    public A2aInputRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the agent asked.</summary>
    public string? Question { get; init; }
}

/// <summary>The agent needs the caller to authenticate in a way an agent workflow cannot.</summary>
public sealed class A2aAuthRequiredException : A2aAgentException
{
    public A2aAuthRequiredException()
    {
    }

    public A2aAuthRequiredException(string message)
        : base(message)
    {
    }

    public A2aAuthRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Policy refused the call.</summary>
public sealed class A2aAgentDeniedException : Exception
{
    public A2aAgentDeniedException()
        : base("The A2A call was refused by policy.")
    {
    }

    public A2aAgentDeniedException(string message)
        : base(message)
    {
    }

    public A2aAgentDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class AgentContextA2aExtensions
{
    /// <summary>
    /// Sends a message to a remote A2A agent as a durable, idempotent step and waits for its answer. The answer is recorded once and returned on every
    /// replay without contacting the agent again. The message id is the step's idempotency key, so a retry after a lost answer is the same message.
    /// When the agent answers with a task, the task id is recorded and polled with durable timers: a restart resumes polling and never sends the
    /// message again. An agent not declared read-only waits for an approval unless the policy allows it.
    /// </summary>
    /// <exception cref="A2aAgentDeniedException">Policy refused the call.</exception>
    /// <exception cref="ApprovalRejectedException">A person rejected the call, or nobody approved in time.</exception>
    /// <exception cref="A2aAgentFailedException">The agent's task failed.</exception>
    /// <exception cref="A2aAgentRejectedException">The agent declined the work.</exception>
    /// <exception cref="A2aAgentCancelledException">The agent's task was cancelled.</exception>
    /// <exception cref="A2aInputRequiredException">The agent asked a question.</exception>
    /// <exception cref="A2aAuthRequiredException">The agent needs authentication an agent cannot provide.</exception>
    /// <exception cref="TimeoutException">The task did not finish within <see cref="A2aCallOptions.Timeout"/>.</exception>
    public static async Task<A2aAgentResult> CallA2aAgentAsync(this IAgentContext context, string agent, string text, A2aCallOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(agent);
        ArgumentNullException.ThrowIfNull(text);
        options ??= new A2aCallOptions();
        IWorkflowContext workflow = context.Workflow;
        DateTimeOffset started = workflow.Now;
        RetryPolicy retry = options.Retry ?? new RetryPolicy
        {
            MaximumAttempts = 3,
            InitialInterval = TimeSpan.FromSeconds(2),
            MaximumInterval = TimeSpan.FromSeconds(30),
            NonRetryableErrorTypes = [nameof(UnknownA2aAgentException), nameof(UntrustedAgentCardException)],
        };

        SendReply reply = await SendAsync(workflow, agent, text, options, retry, approval: null);
        if (reply.Kind == "denied")
        {
            throw new A2aAgentDeniedException($"The call to agent '{agent}' was refused by policy: {reply.Reason}.");
        }

        if (reply.Kind == "approval-required")
        {
            ApprovalDecision decision = await context.RequestApprovalAsync(new ApprovalRequest
            {
                Action = $"Send a message to the A2A agent '{agent}'",
                Details = text,
                Tool = "a2a." + agent,
                Requirement = new ApprovalRequirement(
                    reply.Approvers, reply.Roles, reply.TimeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null, reply.ForbidSelfApproval),
            });
            decision.ThrowIfNotApproved();
            reply = await SendAsync(workflow, agent, text, options, retry, new ApprovalEvidence(decision.ApprovalId, decision.Approver?.Subject ?? ""));
            if (reply.Kind is "denied" or "approval-required")
            {
                throw new A2aAgentDeniedException($"The call to agent '{agent}' is still not allowed after approval: {reply.Reason}.");
            }
        }

        A2aReply current = reply.Reply!;
        TimeSpan wait = TimeSpan.FromSeconds(2);
        while (true)
        {
            switch (current.State)
            {
                case A2aState.Message:
                case A2aState.Completed:
                    return new A2aAgentResult(current.Text, current.DataJson, current.TaskId, current.ContextId);
                case A2aState.Failed:
                    throw new A2aAgentFailedException(current.StatusText ?? current.Text ?? "The agent's task failed.") { TaskId = current.TaskId, ContextId = current.ContextId };
                case A2aState.Rejected:
                    throw new A2aAgentRejectedException(current.StatusText ?? "The agent declined the work.") { TaskId = current.TaskId, ContextId = current.ContextId };
                case A2aState.Canceled:
                    throw new A2aAgentCancelledException("The agent's task was cancelled.") { TaskId = current.TaskId, ContextId = current.ContextId };
                case A2aState.InputRequired:
                    throw new A2aInputRequiredException($"Agent '{agent}' asks: {current.StatusText}") { Question = current.StatusText, TaskId = current.TaskId, ContextId = current.ContextId };
                case A2aState.AuthRequired:
                    throw new A2aAuthRequiredException($"Agent '{agent}' requires authentication.") { TaskId = current.TaskId, ContextId = current.ContextId };
            }

            // Submitted or working: wait on a durable timer, then ask again.
            if (current.TaskId is null)
            {
                throw new A2aAgentException($"Agent '{agent}' reported a task without an id.");
            }

            if (workflow.Now - started >= options.Timeout)
            {
                try
                {
                    await workflow.ExecuteActivityAsync(A2aClientRuntime.CancelActivity, PollInput(agent, current.TaskId), RetryPolicy.Default);
                }
                catch (ActivityFailureException)
                {
                    // best effort
                }

                throw new TimeoutException($"The A2A task {current.TaskId} on '{agent}' did not finish within {options.Timeout}.");
            }

            await workflow.DelayAsync(Clamp(wait, options));
            byte[] bytes = await workflow.ExecuteActivityAsync(A2aClientRuntime.PollActivity, PollInput(agent, current.TaskId), RetryPolicy.Default);
            current = JsonSerializer.Deserialize(bytes, A2aJson.Default.SendReply)!.Reply!;
            wait = TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds * 1.5, options.MaximumPollInterval.TotalSeconds));
        }
    }

    private static byte[] PollInput(string agent, string taskId) => JsonSerializer.SerializeToUtf8Bytes(new PollStep(agent, taskId), A2aJson.Default.PollStep);

    private static TimeSpan Clamp(TimeSpan interval, A2aCallOptions options) =>
        interval < options.MinimumPollInterval ? options.MinimumPollInterval : interval > options.MaximumPollInterval ? options.MaximumPollInterval : interval;

    private static async Task<SendReply> SendAsync(
        IWorkflowContext workflow, string agent, string text, A2aCallOptions options, RetryPolicy retry, ApprovalEvidence? approval)
    {
        byte[] input = JsonSerializer.SerializeToUtf8Bytes(
            new SendStep { Agent = agent, Text = text, DataJson = options.DataJson, TaskId = options.TaskId, ContextId = options.ContextId, Approval = approval }, A2aJson.Default.SendStep);
        byte[] reply = await workflow.ExecuteActivityAsync(A2aClientRuntime.SendActivity, input, retry);
        return JsonSerializer.Deserialize(reply, A2aJson.Default.SendReply)!;
    }
}
