namespace Kairos;

/// <summary>An intent emitted by workflow code during a task; the host turns commands into history events.</summary>
public abstract record WorkflowCommand;

public sealed record ScheduleActivityCommand(int ActivityId, string ActivityType, byte[] Input, RetryPolicy RetryPolicy) : WorkflowCommand;

public sealed record StartTimerCommand(int TimerId, TimeSpan Duration) : WorkflowCommand;

public sealed record CompleteWorkflowCommand(byte[] Result) : WorkflowCommand;

public sealed record FailWorkflowCommand(string ErrorType, string Message) : WorkflowCommand;

/// <summary>A workflow body. Input and output are opaque payload bytes.</summary>
public delegate Task<byte[]> WorkflowFunc(IWorkflowContext context, byte[] input);
