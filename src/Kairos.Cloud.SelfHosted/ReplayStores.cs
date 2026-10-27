namespace Kairos.Cloud.SelfHosted;

/// <summary>
/// A bounded, in-process set of credentials already accepted, each kept until it would be refused as too old anyway. Entries leave in expiry order, so the work to make room is proportional to what expired.
/// A set full of live entries refuses to add: forgetting a live credential would reopen replay. It has no scope: a server uses it before a credential is mapped, to turn a repeat away without asking the
/// cloud, and the shared <see cref="IReplayStore"/> is the authority across instances.
/// </summary>
public sealed class LocalReplayCache(int capacity)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _live = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, DateTimeOffset> _byExpiry = new();

    /// <summary>True when the key is remembered and not yet expired.</summary>
    public bool Contains(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _live.TryGetValue(key, out DateTimeOffset expiresAt) && expiresAt > now;
        }
    }

    public ReplayDecision TryAdd(string key, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        lock (_gate)
        {
            while (_byExpiry.TryPeek(out string? oldest, out DateTimeOffset oldestExpiry) && oldestExpiry <= now)
            {
                _byExpiry.Dequeue();
                if (_live.TryGetValue(oldest, out DateTimeOffset current) && current == oldestExpiry)
                {
                    _live.Remove(oldest);
                }
            }

            if (_live.TryGetValue(key, out DateTimeOffset existing) && existing > now)
            {
                return ReplayDecision.Duplicate;
            }

            if (_live.Count >= capacity)
            {
                return ReplayDecision.Full;
            }

            _live[key] = expiresAt;
            _byExpiry.Enqueue(key, expiresAt);
            return ReplayDecision.Added;
        }
    }
}

/// <summary>An <see cref="IReplayStore"/> in memory: for tests, for one process, and as the reference the shared implementations are checked against. Bounded like <see cref="LocalReplayCache"/>.</summary>
public sealed class InMemoryReplayStore(int capacity = 100_000) : IReplayStore
{
    private readonly LocalReplayCache _cache = new(capacity);

    public ValueTask<ReplayDecision> TryAddAsync(TenantScope scope, string key, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ValueTask.FromResult(_cache.TryAdd($"{scope.TenantId:N}/{scope.NamespaceId:N}/{key}", expiresAt, now));
    }
}

/// <summary>
/// A replay store whose real implementation is supplied after the host has built its database connection: authenticators are made while configuration is read, the connection when the node starts. Until it is bound, adding
/// throws (a node never accepts a credential it cannot remember).
/// </summary>
public sealed class DeferredReplayStore : IReplayStore
{
    private volatile IReplayStore? _inner;

    public bool IsBound => _inner is not null;

    public void Bind(IReplayStore inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public ValueTask<ReplayDecision> TryAddAsync(TenantScope scope, string key, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        (_inner ?? throw new InvalidOperationException("The sign-in replay store is not available yet; the node has not finished starting. The credential is not accepted.")).TryAddAsync(scope, key, expiresAt, now, cancellationToken);
}
