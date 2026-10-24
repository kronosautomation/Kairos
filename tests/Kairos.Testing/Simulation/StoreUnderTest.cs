namespace Kairos.Testing.Simulation;

/// <summary>What the simulator needs besides the store contract: resource counters and the next moment anything becomes runnable.</summary>
public interface IStoreDiagnostics
{
    ValueTask<StoreStats> GetStatsAsync(TenantScope scope);

    ValueTask<DateTimeOffset?> NextWakeupAsync(TenantScope scope, DateTimeOffset now);
}

/// <summary>A store plus its diagnostics; the simulator runs identically against any pair (in-memory, PostgreSQL, ...).</summary>
public sealed record StoreUnderTest(IKairosStore Store, IStoreDiagnostics Diagnostics)
{
    public static StoreUnderTest InMemory()
    {
        var store = new InMemoryKairosStore();
        return new StoreUnderTest(store, new InMemoryDiagnostics(store));
    }

    private sealed class InMemoryDiagnostics(InMemoryKairosStore store) : IStoreDiagnostics
    {
        public ValueTask<StoreStats> GetStatsAsync(TenantScope scope) => ValueTask.FromResult(store.GetStats(scope));

        public ValueTask<DateTimeOffset?> NextWakeupAsync(TenantScope scope, DateTimeOffset now) =>
            ValueTask.FromResult(store.NextWakeup(scope, now));
    }
}
