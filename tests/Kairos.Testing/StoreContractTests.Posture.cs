namespace Kairos.Testing;

/// <summary>What workers say about their payloads is kept per namespace, as the latest time of each kind, and another namespace does not see it.</summary>
public abstract partial class StoreContractTests
{
    [Fact]
    public async Task A_store_keeps_the_latest_time_each_kind_of_worker_report_was_made_and_nothing_before_a_report()
    {
        IKairosStore store = await CreateStoreAsync();
        Assert.Equal(new WorkerPosture(null, null), await store.GetWorkerPostureAsync(A, Ct));
        DateTimeOffset t0 = Time.GetUtcNow();

        await store.NoteWorkerPayloadProtectionAsync(A, payloadsProtected: false, t0, Ct);
        Assert.Equal(new WorkerPosture(t0, null), await store.GetWorkerPostureAsync(A, Ct));

        await store.NoteWorkerPayloadProtectionAsync(A, payloadsProtected: true, t0.AddMinutes(1), Ct);
        await store.NoteWorkerPayloadProtectionAsync(A, payloadsProtected: false, t0.AddMinutes(5), Ct);
        await store.NoteWorkerPayloadProtectionAsync(A, payloadsProtected: false, t0.AddMinutes(2), Ct); // a late report from a slow node does not move the time back
        Assert.Equal(new WorkerPosture(t0.AddMinutes(5), t0.AddMinutes(1)), await store.GetWorkerPostureAsync(A, Ct));
    }

    [Fact]
    public async Task The_time_workers_last_said_they_accept_unverified_approvers_is_kept_and_never_moves_back()
    {
        IKairosStore store = await CreateStoreAsync();
        DateTimeOffset t0 = Time.GetUtcNow();

        await store.NoteWorkerUnverifiedApproversAsync(A, t0.AddMinutes(3), Ct);
        await store.NoteWorkerUnverifiedApproversAsync(A, t0, Ct);

        Assert.Equal(new WorkerPosture(null, null, t0.AddMinutes(3)), await store.GetWorkerPostureAsync(A, Ct));
        Assert.Equal(new WorkerPosture(null, null, null), await store.GetWorkerPostureAsync(B, Ct));
    }

    [Fact]
    public async Task A_worker_report_is_seen_in_its_own_namespace_only()
    {
        IKairosStore store = await CreateStoreAsync();
        var sameTenantOtherNamespace = new TenantScope(A.TenantId, Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a2"));
        DateTimeOffset now = Time.GetUtcNow();

        await store.NoteWorkerPayloadProtectionAsync(A, payloadsProtected: false, now, Ct);

        Assert.Equal(new WorkerPosture(null, null), await store.GetWorkerPostureAsync(B, Ct));
        Assert.Equal(new WorkerPosture(null, null), await store.GetWorkerPostureAsync(sameTenantOtherNamespace, Ct));
        Assert.Equal(new WorkerPosture(now, null), await store.GetWorkerPostureAsync(A, Ct));
    }
}
