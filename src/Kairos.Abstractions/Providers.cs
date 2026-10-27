namespace Kairos;

// Provider interfaces (docs/MULTI-CLOUD-AND-AGENTS.md section 2). Every adapter is async, cancellation-aware, allocation-light and
// must pass the matching suite in Kairos.Conformance. All of them fail closed: anything unknown, expired, tampered with or
// belonging to another tenant is refused, never guessed at.

// ---- keys ---------------------------------------------------------------------------------------------------------

/// <summary>A data key sealed by a key provider. <see cref="KeyId"/> names the master key version that sealed it (rotation).</summary>
public readonly record struct WrappedKey(string KeyId, byte[] Ciphertext);

/// <summary>A wrapped key could not be unwrapped: unknown key, wrong context, or tampered ciphertext. Carries no key material.</summary>
public sealed class KeyUnwrapException : Exception
{
    public KeyUnwrapException()
        : base("The data key could not be unwrapped.")
    {
    }

    public KeyUnwrapException(string message)
        : base(message)
    {
    }

    public KeyUnwrapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Wraps and unwraps data keys for envelope encryption. The master key never leaves the provider and is never sent to Kairos
/// servers. The context passed to each call is authenticated, not secret (tenant, namespace, ...): a key wrapped for one context cannot
/// be unwrapped for another.
/// </summary>
public interface IKeyProvider
{
    ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken);

    /// <exception cref="KeyUnwrapException">Unknown key id, wrong context, or the ciphertext was altered.</exception>
    ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken cancellationToken);
}

/// <summary>Seals and opens payloads on the client side. The server only ever sees what <see cref="ProtectAsync"/> returns.</summary>
public interface IPayloadProtector
{
    /// <summary>Seals <paramref name="plaintext"/>. The result is what the server stores.</summary>
    /// <param name="scope">Tenant and namespace; keys are never shared across scopes.</param>
    /// <param name="plaintext">The payload to seal.</param>
    /// <param name="context">Authenticated binding (run, field, id). A payload opens only under the context it was sealed with.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken);

    /// <summary>Opens a payload sealed by <see cref="ProtectAsync"/> under the same scope and context.</summary>
    /// <param name="scope">Tenant and namespace the payload was sealed for.</param>
    /// <param name="protectedPayload">The sealed payload as stored.</param>
    /// <param name="context">The binding the payload was sealed with.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PayloadProtectionException">Wrong scope or context, unknown key, or altered ciphertext.</exception>
    ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken);
}

public sealed class PayloadProtectionException : Exception
{
    public PayloadProtectionException()
        : base("The payload could not be opened.")
    {
    }

    public PayloadProtectionException(string message)
        : base(message)
    {
    }

    public PayloadProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

// ---- blobs --------------------------------------------------------------------------------------------------------

/// <summary>Content address of an offloaded payload: the SHA-256 of the bytes stored (ciphertext), and their length.</summary>
public readonly record struct BlobRef(string Sha256Hex, long Length);

/// <summary>Stores payloads too large for the history, addressed by their SHA-256. Callers hand it ciphertext. Tenant-scoped.</summary>
public interface IBlobOffload
{
    /// <summary>Stores the bytes (idempotent: the same bytes yield the same reference) and returns their reference.</summary>
    ValueTask<BlobRef> PutAsync(TenantScope scope, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);

    /// <summary>The bytes, verified against the reference, or null when absent in this scope (another tenant's blob is absent too).</summary>
    /// <exception cref="InvalidDataException">The stored bytes no longer match the reference (corruption or tampering).</exception>
    ValueTask<byte[]?> GetAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken);

    ValueTask<bool> DeleteAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken);
}

// ---- identity -----------------------------------------------------------------------------------------------------

/// <summary>Per-namespace roles. Least privilege: each role may do only what its description says.</summary>
public enum Role
{
    /// <summary>Read runs and history.</summary>
    Reader,

    /// <summary>Poll and complete tasks only; cannot start, signal or read other workflows.</summary>
    Worker,

    /// <summary>Start, signal, cancel, terminate, read.</summary>
    Operator,

    /// <summary>Everything within the namespace, including recovery actions.</summary>
    Admin,

    /// <summary>Decide approvals (and read runs to see what is being approved). Cannot start, signal, cancel or terminate.</summary>
    Approver,

    /// <summary>
    /// A worker that also runs durable agents and workflows whose activities talk to conversations of durable agents (nested agents, an executor that calls an agent): everything <see cref="Worker"/> may do, plus start, signal, ask to stop and read
    /// <b>conversations</b> in its own namespace, and nothing else. A server grants it only to credentials it was told to; an ordinary worker does not have it.
    /// </summary>
    AgentWorker,
}

/// <summary>An authenticated caller: one tenant, one namespace, one role.</summary>
public sealed record Principal(TenantScope Scope, Role Role, string Subject, DateTimeOffset? ExpiresAt)
{
    /// <summary>How audit entries name this principal: <c>role:subject</c>.</summary>
    public string AuditName => $"{Role}:{Subject}";
}

/// <summary>What a caller presents. The host validates transport-level proof (TLS chain) before building it.</summary>
public abstract record Credential;

/// <summary>An API key or short-lived token presented as a bearer secret.</summary>
public sealed record BearerCredential(string Secret) : Credential;

/// <summary>A client certificate (already validated by the TLS layer) identified by its SHA-256 thumbprint.</summary>
public sealed record CertificateCredential(string ThumbprintSha256Hex) : Credential;

public enum AuthFailure
{
    None,
    UnknownCredential,
    Expired,
    Revoked,
    Malformed,
}

public sealed record AuthResult(Principal? Principal, AuthFailure Failure)
{
    public bool Succeeded => Principal is not null;

    public static AuthResult Success(Principal principal) => new(principal, AuthFailure.None);

    public static AuthResult Fail(AuthFailure failure) => new(null, failure);
}

/// <summary>Maps a credential to a principal. Never throws for bad input: unknown, expired, revoked and malformed credentials fail closed.</summary>
public interface IWorkerAuthenticator
{
    ValueTask<AuthResult> AuthenticateAsync(Credential credential, DateTimeOffset now, CancellationToken cancellationToken);
}

// ---- database sign-in ---------------------------------------------------------------------------------------------

/// <summary>Which database a token is for: the host, port and role a connection is about to be made as.</summary>
public readonly record struct DatabaseEndpoint(string Host, int Port, string Username);

/// <summary>
/// A short-lived password for the database, from the cloud's identity service (an RDS IAM token, an Entra access token, a Cloud SQL IAM access token), so that no password is configured anywhere.
/// The value is a secret: it is never logged and never put in an exception message. An implementation fails (throws) when it cannot give a token; it never returns an empty one.
/// Use it with <c>NpgsqlDataSourceBuilder.UseTokenPassword(source)</c> (<c>Kairos.Persistence.Postgres</c>), which asks again before the token expires.
/// </summary>
public interface IDatabaseTokenSource
{
    ValueTask<string> GetPasswordAsync(DatabaseEndpoint endpoint, CancellationToken cancellationToken);
}

// ---- events -------------------------------------------------------------------------------------------------------

/// <summary>
/// Metadata about something that happened to a run. Carries no payloads. <see cref="EventId"/> is stable
/// (<c>runId:sequence</c>), so consumers de-duplicate at-least-once delivery.
/// </summary>
public sealed record RunEventNotice(string EventId, RunKey Run, HistoryEventType Type, long Sequence, DateTimeOffset At);

/// <summary>Publishes run events to a bus or webhook. At-least-once; idempotent by <see cref="RunEventNotice.EventId"/>.</summary>
public interface IEventSink
{
    ValueTask PublishAsync(IReadOnlyList<RunEventNotice> events, CancellationToken cancellationToken);
}

/// <summary>Something asked of Kairos from a bus: start a workflow or signal a run. <see cref="EventId"/> de-duplicates redelivery.</summary>
public abstract record InboundEvent(string EventId, TenantScope Scope);

public sealed record StartWorkflowEvent(string EventId, TenantScope Scope, string WorkflowId, string WorkflowType, byte[] Input)
    : InboundEvent(EventId, Scope);

public sealed record SignalRunEvent(string EventId, TenantScope Scope, Guid RunId, string SignalName, byte[] Payload)
    : InboundEvent(EventId, Scope);

/// <summary>A leased inbound message; acknowledge to remove it, abandon (or let the lease lapse) to have it redelivered.</summary>
public interface IInboundMessage
{
    InboundEvent Payload { get; }

    ValueTask AcknowledgeAsync(CancellationToken cancellationToken);

    ValueTask AbandonAsync(CancellationToken cancellationToken);
}

/// <summary>Accepts starts and signals from a bus. At-least-once: unacknowledged messages come back.</summary>
public interface IEventSource
{
    /// <summary>The next available message, or null when none is available right now.</summary>
    ValueTask<IInboundMessage?> ReceiveAsync(CancellationToken cancellationToken);
}
