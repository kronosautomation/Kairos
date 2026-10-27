namespace Kairos.Testing;

/// <summary>What a run is (its workflow id and type): the same for every store.</summary>
public abstract partial class StoreContractTests
{
    [Fact]
    public async Task A_run_says_which_workflow_id_and_type_it_belongs_to_and_an_unknown_run_says_nothing()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "identity-1", "kairos.test/type-1", [], cancellationToken: Ct);
        var key = new RunKey(A.TenantId, A.NamespaceId, start.RunId);

        RunIdentity? identity = await store.GetRunIdentityAsync(key, Ct);

        Assert.Equal(new RunIdentity("identity-1", "kairos.test/type-1"), identity);
        Assert.Null(await store.GetRunIdentityAsync(new RunKey(A.TenantId, A.NamespaceId, Guid.NewGuid()), Ct));
        Assert.Null(await store.GetRunIdentityAsync(new RunKey(B.TenantId, B.NamespaceId, start.RunId), Ct));
    }

    [Fact]
    public async Task A_run_that_continued_as_new_keeps_its_workflow_id_and_type()
    {
        (IKairosStore store, KairosClient client) = await NewAsync();
        StartRunResult start = await client.StartAsync(A, "identity-2", "wf", [1], cancellationToken: Ct);
        await DriveAsync(store, A, Workflows("default", (ctx, input) => input[0] == 1 ? throw ctx.ContinueAsNew(new byte[] { 2 }) : Task.FromResult(input)), Activities());

        RunKey? current = await client.GetCurrentRunAsync(A, "identity-2", Ct);

        Assert.NotNull(current);
        Assert.NotEqual(start.RunId, current.Value.RunId);
        Assert.Equal(new RunIdentity("identity-2", "wf"), await store.GetRunIdentityAsync(current.Value, Ct));
    }
}
