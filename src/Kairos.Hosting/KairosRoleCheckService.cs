using Kairos.Persistence.Postgres;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos;

/// <summary>What the host found about the application's database login at start. Read by the dashboard to show a standing warning; a test double can implement it.</summary>
public interface IDatabaseRoleReport
{
    /// <summary>The login's privileges as found at start, or null when the store is not PostgreSQL or has not started.</summary>
    DatabaseRoleInspection? Inspection { get; }

    /// <summary>True when the login is privileged and the code said <c>AllowPrivilegedDatabaseRole()</c>.</summary>
    bool PrivilegedAllowed { get; }
}

/// <summary>What the host found about the application's database login at start (see <see cref="IDatabaseRoleReport"/>).</summary>
public sealed class KairosDatabaseRole : IDatabaseRoleReport
{
    /// <inheritdoc />
    public DatabaseRoleInspection? Inspection { get; internal set; }

    /// <inheritdoc />
    public bool PrivilegedAllowed { get; internal set; }
}

/// <summary>Refuses to start with a data-plane login that owns Kairos tables or can bypass row level security (see <see cref="KairosOptions.AllowPrivilegedDatabaseRole"/>).</summary>
internal sealed partial class KairosRoleCheckService(KairosOptions.StoreHandle handle, KairosOptions options, KairosDatabaseRole role, ILogger<KairosRoleCheckService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (handle.DataSource is null)
        {
            return;
        }

        DatabaseRoleInspection found = await PostgresProvisioning.InspectRoleAsync(handle.DataSource, cancellationToken).ConfigureAwait(false);
        role.Inspection = found;
        if (!found.Privileged)
        {
            return;
        }

        if (!options.PrivilegedDatabaseRoleAllowed)
        {
            throw new InvalidOperationException(
                "The application's database login can bypass row level security or owns Kairos tables (" + found.Describe() + "). " +
                $"Connect as {PostgresProvisioning.DataPlaneRole} (create it with `kairos db provision`), or call AllowPrivilegedDatabaseRole() to accept this on purpose. Refusing to start.");
        }

        role.PrivilegedAllowed = true;
        LogPrivileged(logger, found.Describe());
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The database login is privileged and AllowPrivilegedDatabaseRole was called: row level security does not protect tenants from this process ({Login})")]
    private static partial void LogPrivileged(ILogger logger, string login);
}
