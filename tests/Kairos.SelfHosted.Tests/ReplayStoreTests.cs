using Kairos.Cloud.SelfHosted;
using Kairos.Conformance;

namespace Kairos.SelfHosted.Tests;

public sealed class InMemoryReplayStoreConformanceTests : ReplayStoreConformance
{
    protected override IReplayStore CreateStore() => new InMemoryReplayStore();
}

public sealed class ReplayStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));

    [Fact]
    public async Task A_bounded_store_full_of_live_entries_refuses_rather_than_forgets_and_has_room_again_after_expiry()
    {
        var store = new InMemoryReplayStore(capacity: 2);

        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(A, "a", Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(A, "b", Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Full, await store.TryAddAsync(A, "c", Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Duplicate, await store.TryAddAsync(A, "a", Now.AddMinutes(5), Now, default)); // still remembered
        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(A, "c", Now.AddMinutes(15), Now.AddMinutes(5), default));
    }

    [Fact]
    public void The_local_cache_turns_a_repeat_away_before_anything_is_asked_and_forgets_at_expiry()
    {
        var cache = new LocalReplayCache(10);
        Assert.False(cache.Contains("k", Now));
        Assert.Equal(ReplayDecision.Added, cache.TryAdd("k", Now.AddMinutes(5), Now));

        Assert.True(cache.Contains("k", Now.AddMinutes(5).AddTicks(-1)));
        Assert.False(cache.Contains("k", Now.AddMinutes(5)));
    }

    [Fact]
    public async Task A_deferred_store_refuses_to_remember_anything_until_it_is_bound_and_then_is_the_store_it_was_given()
    {
        var deferred = new DeferredReplayStore();
        Assert.False(deferred.IsBound);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await deferred.TryAddAsync(A, "k", Now.AddMinutes(5), Now, default));
        var inner = new InMemoryReplayStore();
        deferred.Bind(inner);

        Assert.True(deferred.IsBound);
        Assert.Equal(ReplayDecision.Added, await deferred.TryAddAsync(A, "k", Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Duplicate, await inner.TryAddAsync(A, "k", Now.AddMinutes(5), Now, default)); // the same memory
        Assert.Throws<ArgumentNullException>(() => deferred.Bind(null!));
    }
}
