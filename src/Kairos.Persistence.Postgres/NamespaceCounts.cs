using System.Collections.Concurrent;
using System.Diagnostics;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// An approximate, cheap answer to "is there room in this namespace": the count of open runs or pending timers is taken from the database at most once per time-to-live per namespace and
/// kind on this node (and again whenever the cached figure says the limit is near, so a namespace is never refused on a stale figure), and every grant bumps the cached figure. A limit
/// checked this way can be overshot by what other nodes grant in the same window; it exists to stop a runaway namespace, not to count exactly.
/// </summary>
internal sealed class NamespaceCounts(TimeSpan ttl)
{
    private sealed class Entry
    {
        public long Count;
        public long CountedAt;
    }

    private readonly ConcurrentDictionary<(TenantScope Scope, NamespaceLimit Kind), Entry> _entries = new();

    /// <summary>True when <paramref name="adding"/> more fit under <paramref name="max"/>, and records that they will be added. <paramref name="count"/> reads the real figure, bounded by its argument.</summary>
    public async ValueTask<bool> TryReserveAsync(TenantScope scope, NamespaceLimit kind, int adding, int max, Func<int, CancellationToken, ValueTask<long>> count, CancellationToken cancellationToken)
    {
        Entry entry = _entries.GetOrAdd((scope, kind), static _ => new Entry { CountedAt = long.MinValue });
        bool stale = entry.CountedAt == long.MinValue || Stopwatch.GetElapsedTime(entry.CountedAt) > ttl;
        if (stale || Interlocked.Read(ref entry.Count) + adding > max)
        {
            long real = await count(max, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref entry.Count, real);
            Interlocked.Exchange(ref entry.CountedAt, Stopwatch.GetTimestamp());
        }

        if (Interlocked.Read(ref entry.Count) + adding > max)
        {
            return false;
        }

        _ = Interlocked.Add(ref entry.Count, adding);
        return true;
    }
}
