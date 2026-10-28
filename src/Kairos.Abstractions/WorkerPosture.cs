namespace Kairos;

/// <summary>When workers of a namespace last said how they treat payloads.</summary>
/// <param name="ReadablePayloadsSeenAt">The last time a worker said it stores payloads readable (it was told <c>AllowUnencryptedPayloads</c>); null if none ever did.</param>
/// <param name="ProtectedPayloadsSeenAt">The last time a worker said it encrypts payloads; null if none ever did.</param>
/// <param name="UnverifiedApproversSeenAt">The last time a worker said it accepts approval decisions without verifying who made them (<c>AllowUnverifiedApprovers</c>); null if none ever did.</param>
public readonly record struct WorkerPosture(DateTimeOffset? ReadablePayloadsSeenAt, DateTimeOffset? ProtectedPayloadsSeenAt, DateTimeOffset? UnverifiedApproversSeenAt = null);

/// <summary>
/// Implemented by a store that sits between a worker and a server, so that the worker can tell the server whether it protects payloads. The host calls it once, when the store is created; the
/// store passes the answer on with its calls. A server that sees a worker with <c>false</c> records it per namespace, and its dashboard shows the namespace as holding readable payloads.
/// </summary>
public interface IPayloadPostureReporting
{
    /// <summary>Says whether this process encrypts payloads (a payload protector is configured).</summary>
    void ReportPayloadProtection(bool payloadsProtected);

    /// <summary>Says that this process accepts approval decisions without verifying who made them (the code said <c>AllowUnverifiedApprovers</c>). Called only when that is so; a store that does not pass it on ignores it.</summary>
    void ReportUnverifiedApprovers() { }
}

/// <summary>
/// Implemented by a component registered with <c>KairosOptions.AddActivities</c> or <c>AddActivity</c> that has something to say at start-up about how safely this process was configured. The host
/// logs the warnings when its workers start and, for a worker connected to a server, reports <see cref="ApproversUnverified"/> so that the server can show it.
/// </summary>
public interface IWorkerPostureSource
{
    /// <summary>One sentence per thing the person who configured this process should be told about at start-up. Empty when there is nothing.</summary>
    IReadOnlyList<string> StartupWarnings { get; }

    /// <summary>True when approval decisions are accepted without verifying who made them.</summary>
    bool ApproversUnverified { get; }
}
