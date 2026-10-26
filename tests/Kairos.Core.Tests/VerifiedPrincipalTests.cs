using System.Text;
using Kairos.Testing;

namespace Kairos.Core.Tests;

/// <summary>The principal-first operations of <see cref="KairosClient"/> (cancel, terminate, fail, resume) and the cancel request under client-side encryption.</summary>
// One test here installs a listener on the engine's activity source. A listener makes every run record its trace context in history, so while it exists the deterministic simulations
// running in parallel would see different histories. This collection runs alone.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ActivityListenerGroup
{
    public const string Name = "activity-listener";
}

[Collection(ActivityListenerGroup.Name)]
public sealed class VerifiedPrincipalTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("77777777-0000-0000-0000-000000000001"), Guid.Parse("77777777-0000-0000-0000-0000000000a1"));
    private static readonly TenantScope Other = new(Guid.Parse("77777777-0000-0000-0000-000000000002"), Guid.Parse("77777777-0000-0000-0000-0000000000a2"));

    private static Principal Of(Role role, TenantScope? scope = null) => new(scope ?? Scope, role, "who@example.com", null);

    private sealed record Rig(InMemoryKairosStore Store, KairosClient Client, VirtualTimeProvider Time, RunKey Run);

    private static async Task<Rig> OpenRunAsync()
    {
        var store = new InMemoryKairosStore();
        var time = new VirtualTimeProvider();
        var client = new KairosClient(store, time);
        StartRunResult start = await client.StartAsync(Scope, "wf-1", "wait", [], cancellationToken: CancellationToken.None);
        return new Rig(store, client, time, new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId));
    }

    public static TheoryData<Role, bool, bool, bool> Matrix() => new()
    {
        // role, may cancel, may terminate, may fail
        { Role.Reader, false, false, false },
        { Role.Worker, false, false, false },
        { Role.Approver, false, false, false },
        { Role.Operator, true, true, false },
        { Role.Admin, true, true, true },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Each_operation_needs_its_own_grant_and_a_refusal_changes_nothing(Role role, bool cancel, bool terminate, bool fail)
    {
        CancellationToken ct = CancellationToken.None;
        foreach ((string name, bool allowed, Func<Rig, Task<bool>> act) in new (string, bool, Func<Rig, Task<bool>>)[]
        {
            ("cancel", cancel, async r => await r.Client.CancelRunAsync(Of(role), r.Run, "why", ct)),
            ("terminate", terminate, async r => await r.Client.TerminateRunAsync(Of(role), r.Run, "why", ct)),
            ("fail", fail, async r => await r.Client.FailRunAsync(Of(role), r.Run, "why", ct)),
        })
        {
            Rig rig = await OpenRunAsync();
            if (allowed)
            {
                Assert.True(await act(rig), name);
                Assert.Equal($"{role}:who@example.com", Assert.Single(await rig.Store.GetAuditLogAsync(Scope, ct)).Actor);
            }
            else
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await act(rig));
                Assert.Empty(await rig.Store.GetAuditLogAsync(Scope, ct));
                Assert.Equal(RunState.Running, await rig.Client.GetStateAsync(rig.Run, ct));
                Assert.Empty((await rig.Client.GetHistoryAsync(rig.Run, ct)).OfType<SignalReceived>());
            }
        }
    }

    [Fact]
    public async Task A_principal_of_another_namespace_is_refused_by_the_role_matrix_before_the_store_is_asked()
    {
        Rig rig = await OpenRunAsync();
        CancellationToken ct = CancellationToken.None;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await rig.Client.CancelRunAsync(Of(Role.Admin, Other), rig.Run, "x", ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await rig.Client.ResumeStuckTaskAsync(Of(Role.Admin, Other), rig.Run, ct));
        Assert.False(await rig.Client.ResumeStuckTaskAsync(Of(Role.Admin), rig.Run, ct)); // allowed, but nothing is parked
    }

    [Fact]
    public async Task Cancel_carries_the_request_trace_context_into_the_signal()
    {
        Rig rig = await OpenRunAsync();
        CancellationToken ct = CancellationToken.None;
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == "Kairos",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        Assert.True(await rig.Client.CancelRunAsync(Of(Role.Operator), rig.Run, "x", ct));

        SignalReceived signal = Assert.Single((await rig.Client.GetHistoryAsync(rig.Run, ct)).OfType<SignalReceived>());
        Assert.StartsWith("00-", signal.TraceContext, StringComparison.Ordinal);
    }

    // ---- under client-side encryption ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_cancel_written_by_a_store_that_holds_no_key_is_accepted_by_a_worker_that_does()
    {
        CancellationToken ct = CancellationToken.None;
        var store = new InMemoryKairosStore();
        var time = new VirtualTimeProvider();
        var protector = new ReversingProtector();
        var client = new KairosClient(store, time, payloadProtector: protector);
        StartRunResult start = await client.StartAsync(Scope, "sealed", "wait", Encoding.UTF8.GetBytes("secret input"), cancellationToken: ct);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        var workflows = new WorkflowRegistry().Register("wait", "default", async (ctx, _) =>
        {
            byte[] reason = await ctx.WaitForSignalAsync(ReservedSignals.Cancel);
            return Encoding.UTF8.GetBytes("cancelled with " + reason.Length + " bytes");
        });
        var worker = new WorkerRuntime(store, Scope, workflows, new ActivityRegistry(), time, payloadProtector: protector);
        await worker.ProcessWorkflowTaskAsync(ct);

        // The server side: a plain client without the key. The cancel carries no payload, so there is nothing for it to seal.
        var keyless = new KairosClient(store, time);
        Assert.True(await keyless.CancelRunAsync(Of(Role.Operator), run, "customer asked", ct));
        Assert.True(await worker.ProcessWorkflowTaskAsync(ct));

        Assert.Equal(RunState.Completed, await client.GetStateAsync(run, ct));
        IReadOnlyList<HistoryEvent> opened = await client.GetOpenedHistoryAsync(run, ct);
        Assert.Equal("cancelled with 0 bytes", Encoding.UTF8.GetString(Assert.IsType<WorkflowCompleted>(opened[^1]).Result));
    }

    [Fact]
    public async Task An_empty_unsealed_payload_is_still_refused_for_every_other_signal_name()
    {
        CancellationToken ct = CancellationToken.None;
        var store = new InMemoryKairosStore();
        var time = new VirtualTimeProvider();
        var protector = new ReversingProtector();
        var client = new KairosClient(store, time, payloadProtector: protector);
        StartRunResult start = await client.StartAsync(Scope, "sealed-2", "wait", [], cancellationToken: ct);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);

        // A server (or anyone with database access) writes a signal the workflow would trust, without being able to seal it.
        Assert.True(await new KairosClient(store, time).SignalAsync(run, "approve-payment", [], ct));

        await Assert.ThrowsAsync<PayloadProtectionException>(async () => await client.GetOpenedHistoryAsync(run, ct));
    }

    private sealed class ReversingProtector : IPayloadProtector
    {
        public ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<byte[]>([.. System.Security.Cryptography.SHA256.HashData(context.Span), .. plaintext.ToArray().Reverse()]);

        public ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            byte[] all = protectedPayload.ToArray();
            return all.Length >= 32 && all.AsSpan(0, 32).SequenceEqual(System.Security.Cryptography.SHA256.HashData(context.Span))
                ? ValueTask.FromResult(all.Skip(32).Reverse().ToArray())
                : throw new PayloadProtectionException();
        }
    }
}
