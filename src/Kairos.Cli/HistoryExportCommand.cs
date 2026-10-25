using Kairos.Persistence.Postgres;
using Npgsql;

namespace Kairos.Cli;

/// <summary>The options of <c>kairos history export</c>.</summary>
internal sealed record HistoryExportArguments
{
    public Guid Run { get; init; }

    public string Reason { get; init; } = "";

    public string? Out { get; init; }

    public bool Force { get; init; }

    public bool MetadataOnly { get; init; }

    /// <summary>The name the person gives. Recorded as a claim only: the audit log identifies the exporter by the database login and address.</summary>
    public string Actor { get; init; } = "";

    public TenantScope Scope { get; init; } = KairosOptions.DefaultScope;

    public string ConnectionVariable { get; init; } = "KAIROS_CONNECTION";

    public string? Connection { get; init; }

    public static HistoryExportArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].StartsWith('-'))
        {
            throw new ArgumentException("history export needs the run id: kairos history export <run-id> --reason \"<why>\".");
        }

        if (!Guid.TryParse(args[0], out Guid run))
        {
            throw new ArgumentException("The run id must be a GUID.");
        }

        var result = new HistoryExportArguments { Run = run, Actor = Environment.GetEnvironmentVariable("KAIROS_ACTOR") is { Length: > 0 } a ? a : Environment.UserName };
        Guid? tenant = null, ns = null;
        for (int i = 1; i < args.Count; i++)
        {
            string name = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{name} needs a value.");
            switch (name)
            {
                case "--reason":
                    result = result with { Reason = Value() };
                    break;
                case "--out":
                    result = result with { Out = Value() };
                    break;
                case "--force":
                    result = result with { Force = true };
                    break;
                case "--metadata-only":
                    result = result with { MetadataOnly = true };
                    break;
                case "--actor":
                    result = result with { Actor = Value() };
                    break;
                case "--connection":
                    result = result with { Connection = Value() };
                    break;
                case "--connection-env":
                    result = result with { ConnectionVariable = Value() };
                    break;
                case "--tenant":
                    tenant = Guid.TryParse(Value(), out Guid t) ? t : throw new ArgumentException("--tenant must be a GUID.");
                    break;
                case "--namespace":
                    ns = Guid.TryParse(Value(), out Guid n) ? n : throw new ArgumentException("--namespace must be a GUID.");
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(result.Reason))
        {
            throw new ArgumentException("--reason \"<why>\" is required: it is written to the audit log with your name.");
        }

        if (result.Reason.Length > StoreRules.MaxReasonLength)
        {
            throw new ArgumentException($"--reason is longer than {StoreRules.MaxReasonLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(result.Actor))
        {
            throw new ArgumentException("Say who is exporting with --actor <name> (or KAIROS_ACTOR): it is written to the audit log.");
        }

        if (tenant is null != ns is null)
        {
            throw new ArgumentException("--tenant and --namespace go together.");
        }

        return tenant is { } tt && ns is { } nn ? result with { Scope = new TenantScope(tt, nn) } : result;
    }
}

internal static class HistoryExportCommand
{
    public static async Task<int> RunAsync(HistoryExportArguments args, IKairosStore? store, TimeProvider time, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        string path = args.Out ?? $"{args.Run:D}.khist";
        if (File.Exists(path) && !args.Force)
        {
            await stderr.WriteLineAsync($"'{path}' exists. Choose another file with --out, or overwrite it with --force.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        NpgsqlDataSource? owned = null;
        try
        {
            if (store is null)
            {
                string? connection = args.Connection ?? Environment.GetEnvironmentVariable(args.ConnectionVariable);
                if (string.IsNullOrEmpty(connection))
                {
                    await stderr.WriteLineAsync($"Set {args.ConnectionVariable} (or pass --connection) to a data-plane connection string.").ConfigureAwait(false);
                    return ExitCode.Usage;
                }

                owned = NpgsqlDataSource.Create(connection);
                store = new PostgresKairosStore(owned);
            }

            // The database role is the credential here, and the audit entry names it (login and address come from the database). --actor is only recorded, as a claim.
            HistoryExport? export;
            try
            {
                export = await HistoryExporter.ExportClaimedAsync(
                    store, args.Actor, new RunKey(args.Scope.TenantId, args.Scope.NamespaceId, args.Run), args.Reason, args.MetadataOnly, time, "cli", cancellationToken).ConfigureAwait(false);
            }
            catch (HistoryIntegrityException ex)
            {
                await stderr.WriteLineAsync($"The stored history does not verify (event {ex.Sequence}). The attempt was recorded in the audit log; no file was written.").ConfigureAwait(false);
                return ExitCode.Failed;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
            {
                await stderr.WriteLineAsync("The database role may not write the audit log, so nothing was exported. Use a data-plane role: an export is always recorded.").ConfigureAwait(false);
                return ExitCode.Failed;
            }
            catch (NpgsqlException ex)
            {
                await stderr.WriteLineAsync($"The database could not be read ({ex.GetType().Name}).").ConfigureAwait(false);
                return ExitCode.Usage;
            }
            catch (NotSupportedException)
            {
                await stderr.WriteLineAsync("This store cannot export histories.").ConfigureAwait(false);
                return ExitCode.Usage;
            }

            if (export is null)
            {
                await stderr.WriteLineAsync("There is no such run in this tenant and namespace. Nothing was exported or recorded.").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            await File.WriteAllTextAsync(path, export.FileText, cancellationToken).ConfigureAwait(false);
            await stdout.WriteLineAsync(
                $"Exported {export.History.Count} events of run {args.Run:D} to {path}{(export.MetadataOnly ? " (metadata only)" : "")}. Recorded in the audit log under the database login and address the server saw; the name you gave ({args.Actor}) is stored beside it as a claim.").ConfigureAwait(false);
            if (export.MetadataOnly)
            {
                await stdout.WriteLineAsync("Metadata only: payloads are removed and the file cannot be replayed. It is tamper-evident through a digest chain over the metadata and the digests of what was removed.").ConfigureAwait(false);
            }
            else
            {
                await stdout.WriteLineAsync("The file holds payloads exactly as stored: ciphertext when client-side encryption is on. Treat it as data from production.").ConfigureAwait(false);
            }

            await stdout.WriteLineAsync($"Head hash {Convert.ToHexStringLower(export.History[^1].Hash)} (also in the audit entry). `kairos history verify {path}` checks the file.").ConfigureAwait(false);

            return ExitCode.Passed;
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

internal static class HistoryVerifyCommand
{
    public static async Task<int> RunAsync(string path, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            await stderr.WriteLineAsync($"'{path}' does not exist.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        try
        {
            HistoryFileContents contents = HistoryFile.ParseVerified(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            string kind = contents.MetadataOnly ? "metadata only, digest chain verified" : "hash chain verified";
            await stdout.WriteLineAsync($"OK: {contents.History.Count} events, {kind}, head hash {contents.HeadHash}.").ConfigureAwait(false);
            await stdout.WriteLineAsync("This shows the file is consistent and unedited since it was made. To tie it to the system, compare the head hash with the ExportHistory audit entry.").ConfigureAwait(false);
            return ExitCode.Passed;
        }
        catch (HistoryIntegrityException ex)
        {
            await stderr.WriteLineAsync($"FAILED: the file does not verify (event {ex.Sequence}).").ConfigureAwait(false);
            return ExitCode.Failed;
        }
        catch (FormatException)
        {
            await stderr.WriteLineAsync("FAILED: the file is not a valid history file.").ConfigureAwait(false);
            return ExitCode.Failed;
        }
    }
}
