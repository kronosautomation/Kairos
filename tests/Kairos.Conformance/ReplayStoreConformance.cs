using System.Security.Cryptography;

namespace Kairos.Conformance;

/// <summary>What every <see cref="IReplayStore"/> must do: remember a credential once, keep scopes apart, forget exactly at expiry, and let exactly one of any number of concurrent presentations win.</summary>
public abstract class ReplayStoreConformance
{
    protected static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    protected static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    protected static readonly TenantScope B = new(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002"), Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000b2"));

    protected abstract IReplayStore CreateStore();

    /// <summary>A second store over the same shared memory: another server instance. Null for a store that is only ever one process's.</summary>
    protected virtual IReplayStore? CreateOtherInstance(IReplayStore first) => null;

    private static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public async Task The_first_presentation_is_added_and_the_second_is_a_duplicate()
    {
        IReplayStore store = CreateStore();
        string key = NewKey();

        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(A, key, Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Duplicate, await store.TryAddAsync(A, key, Now.AddMinutes(5), Now.AddSeconds(1), default));
        Assert.Equal(ReplayDecision.Duplicate, await store.TryAddAsync(A, key, Now.AddMinutes(9), Now.AddMinutes(4), default)); // a longer expiry offered later does not make it new
    }

    [Fact]
    public async Task Scopes_and_keys_are_independent()
    {
        IReplayStore store = CreateStore();
        string key = NewKey();

        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(A, key, Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(B, key, Now.AddMinutes(5), Now, default)); // the same key in another scope is another credential
        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(new TenantScope(A.TenantId, B.NamespaceId), key, Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(A, NewKey(), Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Duplicate, await store.TryAddAsync(B, key, Now.AddMinutes(5), Now, default));
    }

    [Fact]
    public async Task An_entry_is_forgotten_exactly_at_its_expiry_and_the_boundary_is_exclusive()
    {
        IReplayStore store = CreateStore();
        string key = NewKey();
        await store.TryAddAsync(A, key, Now.AddMinutes(5), Now, default);

        Assert.Equal(ReplayDecision.Duplicate, await store.TryAddAsync(A, key, Now.AddMinutes(10), Now.AddMinutes(5).AddTicks(-1), default));
        Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(A, key, Now.AddMinutes(10), Now.AddMinutes(5), default)); // at expiry it is gone, and the new presentation is the first of a new window
        Assert.Equal(ReplayDecision.Duplicate, await store.TryAddAsync(A, key, Now.AddMinutes(10), Now.AddMinutes(6), default));
    }

    [Fact]
    public async Task Of_many_concurrent_presentations_of_one_credential_exactly_one_wins()
    {
        IReplayStore store = CreateStore();
        string key = NewKey();

        ReplayDecision[] outcomes = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(async () => await store.TryAddAsync(A, key, Now.AddMinutes(5), Now, default))));

        Assert.Equal(1, outcomes.Count(o => o == ReplayDecision.Added));
        Assert.Equal(199, outcomes.Count(o => o == ReplayDecision.Duplicate));
    }

    [Fact]
    public async Task Many_different_credentials_are_all_added()
    {
        IReplayStore store = CreateStore();

        ReplayDecision[] outcomes = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(async () => await store.TryAddAsync(A, NewKey(), Now.AddMinutes(5), Now, default))));

        Assert.All(outcomes, o => Assert.Equal(ReplayDecision.Added, o));
    }

    [Fact]
    public async Task Any_key_is_fine_and_two_keys_that_differ_in_one_character_are_two_credentials()
    {
        IReplayStore store = CreateStore();
        string[] keys = ["a", "A", "ä", "\u0000", " ", new string('x', 10_000), new string('x', 10_001), "k/with/slashes", "k'with\"quotes"];

        foreach (string key in keys)
        {
            Assert.Equal(ReplayDecision.Added, await store.TryAddAsync(A, key, Now.AddMinutes(5), Now, default));
        }

        foreach (string key in keys)
        {
            Assert.Equal(ReplayDecision.Duplicate, await store.TryAddAsync(A, key, Now.AddMinutes(5), Now, default));
        }
    }

    [Fact]
    public async Task Another_instance_over_the_same_memory_refuses_what_the_first_accepted_and_the_other_way_round()
    {
        IReplayStore first = CreateStore();
        IReplayStore? second = CreateOtherInstance(first);
        if (second is null)
        {
            return;
        }

        string one = NewKey();
        string two = NewKey();
        Assert.Equal(ReplayDecision.Added, await first.TryAddAsync(A, one, Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Duplicate, await second.TryAddAsync(A, one, Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Added, await second.TryAddAsync(A, two, Now.AddMinutes(5), Now, default));
        Assert.Equal(ReplayDecision.Duplicate, await first.TryAddAsync(A, two, Now.AddMinutes(5), Now, default));
    }

    [Fact]
    public async Task Of_concurrent_presentations_across_two_instances_exactly_one_wins()
    {
        IReplayStore first = CreateStore();
        IReplayStore? second = CreateOtherInstance(first);
        if (second is null)
        {
            return;
        }

        string key = NewKey();
        ReplayDecision[] outcomes = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(async () => await (i % 2 == 0 ? first : second).TryAddAsync(A, key, Now.AddMinutes(5), Now, default))));

        Assert.Equal(1, outcomes.Count(o => o == ReplayDecision.Added));
    }
}
