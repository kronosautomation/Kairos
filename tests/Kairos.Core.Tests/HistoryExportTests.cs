using System.Text;
using Kairos.Testing;

namespace Kairos.Core.Tests;

public sealed class HistoryExportTests
{
    private const string Canary = "CANARY-31c9-private-payload";
    private static readonly TenantScope Scope = new(Guid.Parse("55555555-0000-0000-0000-000000000001"), Guid.Parse("55555555-0000-0000-0000-0000000000a1"));
    private static readonly Principal Operator = new(Scope, Role.Operator, "ops@example.com", null);

    private static async Task<(TestEngine Engine, RunKey Run)> RecordAsync()
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("order", async (ctx, input) =>
        {
            byte[] receipt = await ctx.ExecuteActivityAsync("charge", input);
            await ctx.DelayAsync(TimeSpan.FromSeconds(5));
            await ctx.WaitForSignalAsync("note");
            return receipt;
        });
        engine.RegisterActivity("charge", input => Task.FromResult(input));
        Guid id = engine.StartWorkflow("order", Encoding.UTF8.GetBytes(Canary), Scope.TenantId, Scope.NamespaceId);
        await engine.RunAsync(id);
        engine.Signal(id, "note", Encoding.UTF8.GetBytes(Canary));
        await engine.RunAsync(id);
        return (engine, new RunKey(Scope.TenantId, Scope.NamespaceId, id));
    }

    [Fact]
    public async Task An_operator_exports_a_verified_history_and_the_export_is_audited_with_who_and_why()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();

        HistoryExport export = (await HistoryExporter.ExportAsync(engine.Store, Operator, run, "ticket 4411: customer says the charge was doubled", false, engine.Time))!;

        HashChain.Verify(export.History);
        HistoryFileContents loaded = HistoryFile.ParseVerified(export.FileText);
        Assert.False(loaded.MetadataOnly);
        Assert.Equal(export.History.Count, loaded.History.Count);
        AuditEntry entry = Assert.Single(await engine.Store.GetAuditLogAsync(Scope, default));
        Assert.Equal(("ExportHistory", "Operator:ops@example.com", "Operator:ops@example.com", run.RunId), (entry.Action, entry.Actor, entry.VerifiedPrincipal, entry.RunId));
        Assert.Equal($"ticket 4411: customer says the charge was doubled (head {loaded.HeadHash})", entry.Detail); // the audit entry names the last event: the file is tied to the system
    }

    [Fact]
    public async Task The_client_exports_for_a_verified_principal_and_the_audit_entry_names_that_principal()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();
        var client = new KairosClient(engine.Store, engine.Time);

        HistoryExport? export = await client.ExportHistoryAsync(Operator, run, "via the client");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await client.ExportHistoryAsync(new Principal(Scope, Role.Reader, "r", null), run, "no"));

        Assert.NotNull(export);
        AuditEntry entry = Assert.Single(await engine.Store.GetAuditLogAsync(Scope, default));
        Assert.Equal(("Operator:ops@example.com", "Operator:ops@example.com"), (entry.Actor, entry.VerifiedPrincipal));
        Assert.Equal(InMemoryKairosStore.SessionUserName, entry.SessionUser);
    }

    [Fact]
    public async Task A_claimed_name_cannot_change_who_the_audit_log_says_exported()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();

        await HistoryExporter.ExportClaimedAsync(engine.Store, "ops@example.com", run, "first", false, engine.Time);
        await HistoryExporter.ExportClaimedAsync(engine.Store, "ceo@example.com", run, "second", false, engine.Time);
        await HistoryExporter.ExportClaimedAsync(engine.Store, "Operator:alice", run, "third, claiming to be a verified principal", false, engine.Time);

        IReadOnlyList<AuditEntry> audit = await engine.Store.GetAuditLogAsync(Scope, default);
        Assert.Equal(3, audit.Count);
        Assert.All(audit, e => Assert.Equal(("db:in-memory", "in-memory", null), (e.Actor, e.SessionUser, e.VerifiedPrincipal)));
        Assert.Equal(["ops@example.com", "ceo@example.com", "Operator:alice"], audit.Select(e => e.ClaimedActor));
    }

    [Theory]
    [InlineData(Role.Reader)]
    [InlineData(Role.Worker)]
    [InlineData(Role.Approver)]
    public async Task Roles_below_operator_cannot_export_and_nothing_is_recorded(Role role)
    {
        (TestEngine engine, RunKey run) = await RecordAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await HistoryExporter.ExportAsync(engine.Store, new Principal(Scope, role, "someone", null), run, "why", false, engine.Time));

        Assert.Empty(await engine.Store.GetAuditLogAsync(Scope, default));
    }

    [Fact]
    public async Task An_admin_may_export_and_a_principal_of_another_namespace_may_not()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();

        Assert.NotNull(await HistoryExporter.ExportAsync(engine.Store, new Principal(Scope, Role.Admin, "root", null), run, "audit", false, engine.Time));
        var elsewhere = new Principal(new TenantScope(Scope.TenantId, Guid.NewGuid()), Role.Admin, "root", null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await HistoryExporter.ExportAsync(engine.Store, elsewhere, run, "audit", false, engine.Time));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_reason_is_required_and_bounded(string reason)
    {
        (TestEngine engine, RunKey run) = await RecordAsync();

        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await HistoryExporter.ExportAsync(engine.Store, Operator, run, reason, false, engine.Time));
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await HistoryExporter.ExportAsync(engine.Store, Operator, run, new string('x', 501), false, engine.Time));

        Assert.Empty(await engine.Store.GetAuditLogAsync(Scope, default));
    }

    [Fact]
    public async Task A_run_that_does_not_exist_exports_nothing_and_records_nothing()
    {
        (TestEngine engine, _) = await RecordAsync();

        HistoryExport? export = await HistoryExporter.ExportAsync(engine.Store, Operator, new RunKey(Scope.TenantId, Scope.NamespaceId, Guid.NewGuid()), "why", false, engine.Time);

        Assert.Null(export);
        Assert.Empty(await engine.Store.GetAuditLogAsync(Scope, default));
    }

    [Fact]
    public async Task A_stored_history_that_does_not_verify_is_not_exported_but_the_attempt_is_recorded()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();
        var tampering = new TamperingStore(engine.Store);

        await Assert.ThrowsAsync<HistoryIntegrityException>(async () => await HistoryExporter.ExportAsync(tampering, Operator, run, "why", false, engine.Time));

        Assert.Single(await engine.Store.GetAuditLogAsync(Scope, default));
    }

    [Fact]
    public async Task A_metadata_only_export_has_no_payload_and_still_proves_order_and_linkage()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();

        HistoryExport export = (await HistoryExporter.ExportAsync(engine.Store, Operator, run, "shape only", metadataOnly: true, engine.Time))!;

        Assert.True(export.MetadataOnly);
        Assert.DoesNotContain(Canary, export.FileText, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary))[..20], export.FileText, StringComparison.Ordinal);
        Assert.All(export.History, e => Assert.True(e switch
        {
            WorkflowStarted s => s.Input.Length == 0,
            ActivityScheduled s => s.Input.Length == 0,
            ActivityCompleted c => c.Result.Length == 0,
            SignalReceived s => s.Payload.Length == 0,
            WorkflowCompleted c => c.Result.Length == 0,
            _ => true,
        }));
        HistoryFileContents loaded = HistoryFile.ParseVerified(export.FileText);
        Assert.True(loaded.MetadataOnly);
        Assert.Equal($"metadata only: shape only (head {loaded.HeadHash})", Assert.Single(await engine.Store.GetAuditLogAsync(Scope, default)).Detail);
        Assert.Equal(export.History.Count, loaded.PayloadDigests!.Count);
        Assert.Contains(loaded.PayloadDigests, d => d.Length == 32); // what was removed is pinned by its digest
        Assert.DoesNotContain(Canary, string.Join(' ', loaded.PayloadDigests.Select(Convert.ToBase64String)), StringComparison.Ordinal);
    }

    private static async Task<(string Text, string[] Lines, int FirstEvent)> MetadataExportAsync()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();
        HistoryExport export = (await HistoryExporter.ExportAsync(engine.Store, Operator, run, "shape only", metadataOnly: true, engine.Time))!;
        string[] lines = export.FileText.Split('\n');
        return (export.FileText, lines, Array.FindIndex(lines, l => l.StartsWith("event: ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_metadata_only_file_whose_events_are_reordered_dropped_or_inserted_is_rejected()
    {
        (_, string[] lines, int first) = await MetadataExportAsync();

        string[] swapped = [.. lines];
        (swapped[first + 1], swapped[first + 2]) = (swapped[first + 2], swapped[first + 1]);
        Assert.Throws<HistoryIntegrityException>(() => HistoryFile.ParseVerified(string.Join('\n', swapped)));

        Assert.Throws<HistoryIntegrityException>(() => HistoryFile.ParseVerified(string.Join('\n', lines.Where((_, i) => i != first + 2))));
        Assert.Throws<HistoryIntegrityException>(() => HistoryFile.ParseVerified(string.Join('\n', lines.Take(first + 2).Append(lines[first + 1]).Concat(lines.Skip(first + 2)))));
    }

    [Fact]
    public async Task A_metadata_only_file_with_an_edited_name_digest_or_hash_is_rejected()
    {
        (string text, string[] lines, int first) = await MetadataExportAsync();
        HistoryFileContents real = HistoryFile.Parse(text);
        int scheduled = real.History.ToList().FindIndex(e => e is ActivityScheduled);

        // 1. Rename the activity in the event body and leave everything else alone.
        string[] renamed = [.. lines];
        string[] parts = renamed[first + scheduled].Split(' ');
        parts[1] = Convert.ToBase64String(HistoryEventCodec.Encode(((ActivityScheduled)real.History[scheduled]) with { ActivityType = "refund" }));
        renamed[first + scheduled] = string.Join(' ', parts);
        Assert.Throws<HistoryIntegrityException>(() => HistoryFile.ParseVerified(string.Join('\n', renamed)));

        // 2. Swap in a digest for a payload that never existed.
        string[] redigested = [.. lines];
        parts = redigested[first + scheduled].Split(' ');
        parts[4] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("something else"u8));
        redigested[first + scheduled] = string.Join(' ', parts);
        Assert.Throws<HistoryIntegrityException>(() => HistoryFile.ParseVerified(string.Join('\n', redigested)));

        // 3. Change a stored hash of the system chain.
        string[] rehashed = [.. lines];
        parts = rehashed[first + scheduled].Split(' ');
        parts[3] = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData("forged"u8));
        rehashed[first + scheduled] = string.Join(' ', parts);
        Assert.Throws<HistoryIntegrityException>(() => HistoryFile.ParseVerified(string.Join('\n', rehashed)));
    }

    [Fact]
    public async Task A_forged_metadata_only_export_that_only_has_consistent_linkage_is_rejected()
    {
        (string text, _, _) = await MetadataExportAsync();
        HistoryFileContents real = HistoryFile.Parse(text);

        // The forger renames an activity, then re-chains the redacted events so every hash links to the one before: all that the old check (linkage) looked at.
        var forged = new List<HistoryEvent>();
        byte[] prev = HashChain.GenesisHash.ToArray();
        foreach (HistoryEvent e in real.History)
        {
            HistoryEvent sealedEvent = HashChain.Seal(e is ActivityScheduled s ? s with { ActivityType = "refund" } : e, prev);
            forged.Add(sealedEvent);
            prev = sealedEvent.Hash;
        }

        HashChain.VerifyLinkage(forged); // the forgery would have passed linkage

        // Presented the way a metadata-only file looks (five fields), with the digests and chain values copied from the real file: rejected.
        var lines = new List<string> { "# kairos-history forged", "# note: ", "replay-with: ", "outcome: Completed", "mode: metadata" };
        for (int i = 0; i < forged.Count; i++)
        {
            lines.Add("event: " + Convert.ToBase64String(HistoryEventCodec.Encode(forged[i])) + " " + Convert.ToBase64String(forged[i].PrevHash) + " " + Convert.ToBase64String(forged[i].Hash)
                + " " + (real.PayloadDigests![i].Length == 0 ? "-" : Convert.ToBase64String(real.PayloadDigests[i])) + " " + Convert.ToBase64String(real.DigestChain![i]));
        }

        Assert.Throws<HistoryIntegrityException>(() => HistoryFile.ParseVerified(string.Join('\n', lines)));

        // With the mode line removed, five-field lines are not a valid file of either kind.
        Assert.Throws<FormatException>(() => HistoryFile.ParseVerified(string.Join('\n', lines.Where(l => l != "mode: metadata"))));
    }

    [Fact]
    public async Task A_fully_recomputed_forgery_is_consistent_but_cannot_match_the_head_recorded_by_the_system()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();
        HistoryExport export = (await HistoryExporter.ExportAsync(engine.Store, Operator, run, "shape only", metadataOnly: true, engine.Time))!;
        IReadOnlyList<HistoryEvent> stored = await engine.Store.GetHistoryAsync(run, default);

        // Someone with the whole algorithm can rebuild a consistent file around invented events. That is true of every hash chain; what exposes it is the anchor.
        var invented = new List<HistoryEvent>();
        byte[] prev = HashChain.GenesisHash.ToArray();
        foreach (HistoryEvent e in stored)
        {
            HistoryEvent sealedEvent = HashChain.Seal(e is ActivityScheduled s ? s with { ActivityType = "refund" } : e, prev);
            invented.Add(sealedEvent);
            prev = sealedEvent.Hash;
        }

        HistoryFileContents forged = HistoryFile.ParseVerified(HistoryFile.SerializeMetadataOnly(invented, "forged"));
        HistoryFileContents genuine = HistoryFile.ParseVerified(export.FileText);
        AuditEntry audit = Assert.Single(await engine.Store.GetAuditLogAsync(Scope, default));

        Assert.EndsWith($"(head {genuine.HeadHash})", audit.Detail, StringComparison.Ordinal);
        Assert.NotEqual(genuine.HeadHash, forged.HeadHash);
        Assert.DoesNotContain(forged.HeadHash, audit.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_edited_after_export_fails_verification_on_load()
    {
        (TestEngine engine, RunKey run) = await RecordAsync();
        HistoryExport export = (await HistoryExporter.ExportAsync(engine.Store, Operator, run, "why", false, engine.Time))!;
        List<HistoryEvent> history = [.. export.History];
        int at = history.FindIndex(e => e is ActivityScheduled);
        history[at] = ((ActivityScheduled)history[at]) with { Input = [9, 9, 9] };
        string forged = HistoryFile.Serialize(history, "forged");

        HistoryIntegrityException error = Assert.Throws<HistoryIntegrityException>(() => HistoryFile.ParseVerified(forged));

        Assert.Equal(history[at].Sequence, error.Sequence);
        Assert.DoesNotContain(Canary, error.Message, StringComparison.Ordinal);
    }

    private sealed class TamperingStore(IKairosStore inner) : ForwardingStore(inner)
    {
        public override async ValueTask<IReadOnlyList<HistoryEvent>?> ExportHistoryAsync(
            RunKey run, string claimedActor, string? verifiedPrincipal, string reason, bool metadataOnly, DateTimeOffset now, CancellationToken cancellationToken)
        {
            IReadOnlyList<HistoryEvent>? real = await base.ExportHistoryAsync(run, claimedActor, verifiedPrincipal, reason, metadataOnly, now, cancellationToken);
            if (real is null)
            {
                return null;
            }

            List<HistoryEvent> copy = [.. real];
            int at = copy.FindIndex(e => e is ActivityScheduled);
            copy[at] = ((ActivityScheduled)copy[at]) with { Input = [1] };
            return copy;
        }
    }
}
