using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

    /// <summary>Base address of a Kairos server (<c>https://host</c>). When set, the export goes through the server: it checks the sign-in and the operator role and writes the audit entry.</summary>
    public string? Server { get; init; }

    /// <summary>The environment variable that holds the bearer token for <see cref="Server"/> (a session token or an identity provider token).</summary>
    public string TokenVariable { get; init; } = "KAIROS_TOKEN";

    /// <summary>The namespace to export from on a server. The tenant comes from the credential.</summary>
    public Guid? ServerNamespace { get; init; }

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
                case "--server":
                    result = result with { Server = Value() };
                    break;
                case "--token-env":
                    result = result with { TokenVariable = Value() };
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

        if (result.Server is not null)
        {
            if (tenant is not null || result.Connection is not null)
            {
                throw new ArgumentException("--server does not go with --tenant or --connection: the server takes the tenant from your sign-in and holds the database credential.");
            }

            if (ns is null)
            {
                throw new ArgumentException("--server needs --namespace <guid>.");
            }

            if (!Uri.TryCreate(result.Server, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            {
                throw new ArgumentException("--server must be an https:// address.");
            }

            return result with { ServerNamespace = ns };
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
    /// <summary>The database role an export straight from the database must be made with. The application's own (data-plane) credential is refused: it belongs to the program, not to a person.</summary>
    internal const string RequiredRole = PostgresProvisioning.MigratorRole;

    public static async Task<int> RunAsync(
        HistoryExportArguments args, IKairosStore? store, TimeProvider time, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken, HttpMessageHandler? serverHandler = null)
    {
        string path = args.Out ?? $"{args.Run:D}.khist";
        if (File.Exists(path) && !args.Force)
        {
            await stderr.WriteLineAsync($"'{path}' exists. Choose another file with --out, or overwrite it with --force.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        if (args.Server is not null)
        {
            return await ExportThroughServerAsync(args, path, serverHandler, stdout, stderr, cancellationToken).ConfigureAwait(false);
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
                if (!await HasRequiredRoleAsync(owned, cancellationToken).ConfigureAwait(false))
                {
                    await stderr.WriteLineAsync(
                        $"Refused: a direct export must be made with the {RequiredRole} credential, not the application's. " +
                        "Either run it as the migrator login (the export is audited under that login and your --reason), or sign in to a Kairos server with the operator role: --server <https://host> --namespace <guid> (token in KAIROS_TOKEN).").ConfigureAwait(false);
                    return ExitCode.Usage;
                }

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

    private static async Task<bool> HasRequiredRoleAsync(NpgsqlDataSource source, CancellationToken cancellationToken)
    {
        try
        {
            await using NpgsqlConnection conn = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT pg_has_role(current_user, @role, 'MEMBER')", conn);
            cmd.Parameters.AddWithValue("role", RequiredRole);
            return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static async Task<int> ExportThroughServerAsync(
        HistoryExportArguments args, string path, HttpMessageHandler? handler, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        string? token = Environment.GetEnvironmentVariable(args.TokenVariable);
        if (string.IsNullOrWhiteSpace(token))
        {
            await stderr.WriteLineAsync($"Set {args.TokenVariable} to the token you signed in to the server with (the operator role is needed).").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        using HttpClient http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(args.Server!), $"/v1/namespaces/{args.ServerNamespace:D}/runs/{args.Run:D}/export"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new Dictionary<string, object> { ["reason"] = args.Reason, ["metadataOnly"] = args.MetadataOnly }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await stderr.WriteLineAsync("The server could not be reached.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await stderr.WriteLineAsync("The server refused the export: sign in with the operator role for this namespace. Nothing was exported.").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await stderr.WriteLineAsync("There is no such run in this namespace. Nothing was exported.").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            if (!response.IsSuccessStatusCode)
            {
                await stderr.WriteLineAsync($"The server answered {(int)response.StatusCode}. Nothing was exported.").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            HistoryFileContents contents;
            try
            {
                contents = HistoryFile.ParseVerified(text); // the server verifies before it answers; a file is never written unless it verifies here too
            }
            catch (Exception ex) when (ex is HistoryIntegrityException or FormatException)
            {
                await stderr.WriteLineAsync("The server's answer is not a history that verifies. No file was written.").ConfigureAwait(false);
                return ExitCode.Failed;
            }

            await File.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
            await stdout.WriteLineAsync(
                $"Exported {contents.History.Count} events of run {args.Run:D} to {path}{(contents.MetadataOnly ? " (metadata only)" : "")} through the server. The server recorded it in the audit log under your verified sign-in, with your reason.").ConfigureAwait(false);
            await stdout.WriteLineAsync($"Head hash {contents.HeadHash}. `kairos history verify {path}` checks the file.").ConfigureAwait(false);
            return ExitCode.Passed;
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
