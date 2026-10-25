using System.Text;
using Kairos.Cli;
using Kairos.Testing;

namespace Kairos.Cli.Tests;

public sealed class HistoryExportCliTests : IDisposable
{
    private static readonly TenantScope Scope = new(Guid.Parse("88888888-0000-0000-0000-000000000001"), Guid.Parse("88888888-0000-0000-0000-0000000000a1"));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairos-export-" + Guid.NewGuid().ToString("N"));

    public HistoryExportCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<(TestEngine Engine, Guid Run)> RecordAsync()
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("order", Orders.Original);
        engine.RegisterActivity("charge", input => Task.FromResult(input));
        Guid run = engine.StartWorkflow("order", Encoding.UTF8.GetBytes(Orders.Canary), Scope.TenantId, Scope.NamespaceId);
        await engine.RunAsync(run);
        return (engine, run);
    }

    private async Task<(int Exit, string Out, string Err, string File)> ExportAsync(IKairosStore store, TimeProvider time, Guid run, params string[] extra)
    {
        string file = Path.Combine(_dir, $"{run:N}.khist");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        HistoryExportArguments args = HistoryExportArguments.Parse(
            [run.ToString(), "--out", file, "--actor", "ops@example.com", "--tenant", Scope.TenantId.ToString(), "--namespace", Scope.NamespaceId.ToString(), .. extra]);
        int exit = await HistoryExportCommand.RunAsync(args, store, time, stdout, stderr, default);
        return (exit, stdout.ToString(), stderr.ToString(), file);
    }

    [Fact]
    public async Task An_export_writes_a_verified_file_and_an_audit_entry_with_the_name_and_the_reason()
    {
        (TestEngine engine, Guid run) = await RecordAsync();

        (int exit, string output, _, string file) = await ExportAsync(engine.Store, engine.Time, run, "--reason", "ticket 4411");

        Assert.Equal(ExitCode.Passed, exit);
        Assert.Contains("Recorded in the audit log as ops@example.com", output, StringComparison.Ordinal);
        Assert.False(HistoryFile.ParseVerified(await File.ReadAllTextAsync(file)).MetadataOnly);
        AuditEntry entry = Assert.Single(await engine.Store.GetAuditLogAsync(Scope, default));
        Assert.Equal(("ExportHistory", "ops@example.com"), (entry.Action, entry.Actor));
        Assert.StartsWith("ticket 4411 (head ", entry.Detail, StringComparison.Ordinal);
        Assert.Contains(HistoryFile.Parse(await File.ReadAllTextAsync(file)).HeadHash, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_metadata_only_export_has_no_payload_and_says_so_in_the_audit_log()
    {
        (TestEngine engine, Guid run) = await RecordAsync();

        (int exit, string output, _, string file) = await ExportAsync(engine.Store, engine.Time, run, "--reason", "shape", "--metadata-only");

        Assert.Equal(ExitCode.Passed, exit);
        string text = await File.ReadAllTextAsync(file);
        Assert.Contains("mode: metadata", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Orders.Canary, text, StringComparison.Ordinal);
        Assert.Contains("digest chain", output, StringComparison.Ordinal);
        Assert.StartsWith("metadata only: ", Assert.Single(await engine.Store.GetAuditLogAsync(Scope, default)).Detail, StringComparison.Ordinal);

        // `history verify` accepts it, and rejects it once a name in it has been edited.
        var verifyOut = new StringWriter();
        Assert.Equal(ExitCode.Passed, await HistoryVerifyCommand.RunAsync(file, verifyOut, new StringWriter(), default));
        Assert.Contains("metadata only, digest chain verified", verifyOut.ToString(), StringComparison.Ordinal);
        HistoryFileContents real = HistoryFile.Parse(text);
        int scheduled = real.History.ToList().FindIndex(e => e is ActivityScheduled);
        string[] lines = text.Split('\n');
        int first = Array.FindIndex(lines, l => l.StartsWith("event: ", StringComparison.Ordinal));
        string[] parts = lines[first + scheduled].Split(' ');
        parts[1] = Convert.ToBase64String(HistoryEventCodec.Encode(((ActivityScheduled)real.History[scheduled]) with { ActivityType = "refund" }));
        lines[first + scheduled] = string.Join(' ', parts);
        await File.WriteAllTextAsync(file, string.Join('\n', lines));
        Assert.Equal(ExitCode.Failed, await HistoryVerifyCommand.RunAsync(file, new StringWriter(), new StringWriter(), default));
    }

    [Fact]
    public async Task Without_a_reason_nothing_is_exported_and_nothing_is_recorded()
    {
        (TestEngine engine, Guid run) = await RecordAsync();

        ArgumentException none = Assert.Throws<ArgumentException>(() => HistoryExportArguments.Parse([run.ToString(), "--actor", "ops"]));
        ArgumentException blank = Assert.Throws<ArgumentException>(() => HistoryExportArguments.Parse([run.ToString(), "--actor", "ops", "--reason", "   "]));

        Assert.Contains("--reason", none.Message, StringComparison.Ordinal);
        Assert.Contains("--reason", blank.Message, StringComparison.Ordinal);
        Assert.Empty(await engine.Store.GetAuditLogAsync(Scope, default));
    }

    [Fact]
    public async Task An_existing_file_is_not_overwritten_without_force_and_a_missing_run_exports_nothing()
    {
        (TestEngine engine, Guid run) = await RecordAsync();
        (int first, _, _, string file) = await ExportAsync(engine.Store, engine.Time, run, "--reason", "first");
        await File.WriteAllTextAsync(file, "keep me");

        (int again, _, string error, _) = await ExportAsync(engine.Store, engine.Time, run, "--reason", "second");
        Assert.Equal(ExitCode.Passed, first);
        Assert.Equal(ExitCode.Usage, again);
        Assert.Contains("--force", error, StringComparison.Ordinal);
        Assert.Equal("keep me", await File.ReadAllTextAsync(file));
        Assert.Single(await engine.Store.GetAuditLogAsync(Scope, default)); // the refused attempt did not read or record anything

        (int forced, _, _, _) = await ExportAsync(engine.Store, engine.Time, run, "--reason", "third", "--force");
        Assert.Equal(ExitCode.Passed, forced);

        (int missing, _, string missingError, _) = await ExportAsync(engine.Store, engine.Time, Guid.NewGuid(), "--reason", "no such run");
        Assert.Equal(ExitCode.Failed, missing);
        Assert.Contains("no such run", missingError, StringComparison.Ordinal);
        Assert.Equal(2, (await engine.Store.GetAuditLogAsync(Scope, default)).Count);
    }

    [Fact]
    public async Task The_command_line_routes_history_export_and_reports_usage_errors()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Type cli = typeof(ReplayCheckArguments).Assembly.GetType("Kairos.Cli.Cli")!;
        async Task<int> Run(params string[] args) =>
            await (Task<int>)cli.GetMethod("RunAsync", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!.Invoke(null, [args, stdout, stderr, CancellationToken.None])!;

        Assert.Equal(ExitCode.Usage, await Run("history"));
        Assert.Equal(ExitCode.Usage, await Run("history", "export"));
        Assert.Equal(ExitCode.Usage, await Run("history", "export", "not-a-guid", "--reason", "x"));
        Assert.Equal(ExitCode.Usage, await Run("history", "export", Guid.NewGuid().ToString())); // no reason
        Assert.Contains("--reason", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCode.Usage, await Run("history", "export", Guid.NewGuid().ToString(), "--reason", "why", "--connection-env", "KAIROS_SURELY_NOT_SET"));
    }
}
