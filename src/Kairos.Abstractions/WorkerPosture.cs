namespace Kairos;

/// <summary>When workers of a namespace last said how they treat payloads.</summary>
/// <param name="ReadablePayloadsSeenAt">The last time a worker said it stores payloads readable (it was told <c>AllowUnencryptedPayloads</c>); null if none ever did.</param>
/// <param name="ProtectedPayloadsSeenAt">The last time a worker said it encrypts payloads; null if none ever did.</param>
public readonly record struct WorkerPosture(DateTimeOffset? ReadablePayloadsSeenAt, DateTimeOffset? ProtectedPayloadsSeenAt);

/// <summary>
/// Implemented by a store that sits between a worker and a server, so that the worker can tell the server whether it protects payloads. The host calls it once, when the store is created; the
/// store passes the answer on with its calls. A server that sees a worker with <c>false</c> records it per namespace, and its dashboard shows the namespace as holding readable payloads.
/// </summary>
public interface IPayloadPostureReporting
{
    /// <summary>Says whether this process encrypts payloads (a payload protector is configured).</summary>
    void ReportPayloadProtection(bool payloadsProtected);
}
