using Kairos.Cli;
using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// <c>kairos history export</c> straight from the database is a person's act, so it refuses the application's own credential: it runs only as the migrator login, with a reason,
/// and the audit entry names that login. Everyone else goes through a server, which checks the operator role.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class HistoryExportCredentialTests(PostgresFixture fixture) : IDisposable
{
    private static readonly TenantScope A = new(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"), Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000a1"));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairos-export-cred-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private async Task<(int Exit, string Out, string Err, string File)> ExportAsync(string connection, Guid run)
    {
        _ = Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, $"{run:N}-{Guid.NewGuid():N}.khist");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        HistoryExportArguments args = HistoryExportArguments.Parse(
            [run.ToString(), "--reason", "ticket 77", "--actor", "ops", "--out", file, "--connection", connection, "--tenant", A.TenantId.ToString(), "--namespace", A.NamespaceId.ToString()]);
        int exit = await HistoryExportCommand.RunAsync(args, store: null, TimeProvider.System, stdout, stderr, default);
        return (exit, stdout.ToString(), stderr.ToString(), file);
    }

    [Fact]
    public async Task The_application_credential_is_refused_and_the_migrator_credential_exports_under_its_own_login()
    {
        await using TestDatabase database = await fixture.NewDatabaseAsync();
        var client = new KairosClient(database.NewStore(), TimeProvider.System);
        StartRunResult started = await client.StartAsync(A, "export-credential", "t", [1]);

        (int refused, _, string refusal, string refusedFile) = await ExportAsync(database.DataPlaneConnectionString, started.RunId);
        Assert.Equal(ExitCode.Usage, refused);
        Assert.Contains("kairos_migrator", refusal, StringComparison.Ordinal);
        Assert.False(File.Exists(refusedFile));
        Assert.DoesNotContain(await database.NewStore().GetAuditLogAsync(A, default), e => e.Action == "ExportHistory");

        (int exit, _, string error, string file) = await ExportAsync(database.MigratorConnectionString, started.RunId);

        Assert.True(exit == ExitCode.Passed, error);
        Assert.True(File.Exists(file));
        AuditEntry entry = Assert.Single(await database.NewStore().GetAuditLogAsync(A, default), e => e.Action == "ExportHistory");
        Assert.Equal(PostgresProvisioning.MigratorRole, entry.SessionUser);
        Assert.Equal("ops", entry.ClaimedActor);
        Assert.StartsWith("ticket 77", entry.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_administrator_credential_is_refused_although_it_is_a_member_of_every_role()
    {
        await using TestDatabase database = await fixture.NewDatabaseAsync();
        var client = new KairosClient(database.NewStore(), TimeProvider.System);
        StartRunResult started = await client.StartAsync(A, "export-credential", "t", [1]);

        // The container's administrator is a superuser: pg_has_role says it is a member of kairos_migrator. The export must still refuse it.
        (int exit, _, string refusal, string file) = await ExportAsync(fixture.ConnectionString(database.Name, null, null), started.RunId);

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains("kairos_migrator", refusal, StringComparison.Ordinal);
        Assert.False(File.Exists(file));
        Assert.DoesNotContain(await database.NewStore().GetAuditLogAsync(A, default), e => e.Action == "ExportHistory");
    }

    [Fact]
    public async Task A_read_only_credential_is_refused_too()
    {
        await using TestDatabase database = await fixture.NewDatabaseAsync();

        (int exit, _, string refusal, _) = await ExportAsync(database.ReadOnlyConnectionString, Guid.NewGuid());

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains("Refused", refusal, StringComparison.Ordinal);
    }
}
