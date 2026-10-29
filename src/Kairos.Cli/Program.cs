using Kairos.Cli;

return await Cli.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

namespace Kairos.Cli
{
    /// <summary>The <c>kairos</c> command.</summary>
    internal static class Cli
    {
        private const string Usage = """
            kairos: Kairos command line

              kairos db provision [--admin-connection-env <name>] [--migrator-password-env <name>] [--dataplane-password-env <name>]
                                  [--controlplane-password-env <name>] [--readonly-password-env <name>]

              kairos db migrate [--connection-env <name>] [--lock-timeout <seconds>]

              kairos replay-check --build <assembly> [--connection <string> | set KAIROS_CONNECTION] [--corpus <dir>]
                                  [--tenant <guid> --namespace <guid>] [--sample <n|all>] [--since <30m|12h|7d|date>]
                                  [--strict-coverage] [--format text|json] [--setup <type>]

              kairos history export <run-id> --reason "<why>" [--out <file>] [--metadata-only] [--actor <name>] [--force]
                                    [--connection <migrator connection string> | set KAIROS_CONNECTION] [--tenant <guid> --namespace <guid>]
                                    or, through a server: --server <https://host> --namespace <guid>  (sign-in token in KAIROS_TOKEN, or --token-env <name>)

              kairos history verify <file>

              kairos audit export --server <https://host> --namespace <guid> --reason "<why>" [--from <date>] [--to <date>] [--run <guid>]... [--out <dir>] [--token-env <name>]

              kairos audit verify <bundle> --key <public key file> [--kid <key id>] [--history <file>]... [--require-opened]

            db provision is first-time setup, run once per database with an administrator connection (KAIROS_ADMIN_CONNECTION): it creates the four Kairos roles and lets kairos_migrator create the
            schema. Passwords come from the environment (KAIROS_MIGRATOR_PASSWORD, KAIROS_DATAPLANE_PASSWORD, KAIROS_CONTROLPLANE_PASSWORD, KAIROS_READONLY_PASSWORD), never from the command line.
            db migrate applies pending schema migrations as kairos_migrator (KAIROS_CONNECTION), as one transaction behind a database lock: run it as a deploy step before the new version starts. Running it
            twice, or from several machines at once, is harmless. Exit codes: 0 done, 1 failed (rolled back), 2 could not run.

            audit export asks a Kairos server for signed audit evidence of one namespace and writes the bundle to a directory. You sign in to the server (token in KAIROS_TOKEN); the administrator
            role is needed; the server signs with a key of its own, audits every history it reads under your sign-in, and holds no payload key, so sealed steps are reported as not opened.

            audit verify checks a signed audit evidence bundle offline: the signature under the public key you supply (the key of the customer who signed, from their key service), the
            evidence against the signed manifest, every record's link, and the chain heads. Each --history file (from history export, full or metadata only) is checked against the recorded
            head of its run. It prints what it checked, never a record's contents. Exit codes: 0 verified, 1 not verified, 2 could not run.

            history verify checks a history file (the hash chain, or the digest chain of a metadata-only file) and prints its head hash.

            history export copies one run's history to a file (ciphertext as stored) for local replay under a debugger. A reason is required and the export is audited. Through a
            server, you sign in and need the operator role. Straight from the database, the connection must be the kairos_migrator login: the application's credential is refused.

            replay-check replays recorded histories against the candidate build and exits 1 if any diverges, printing the workflow type, the run and the first
            diverging command. It prints names, ids and sizes only, never payload contents. Exit codes: 0 passed, 1 failed, 2 could not run.
            """;

        public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
                return args.Length == 0 ? ExitCode.Usage : ExitCode.Passed;
            }

            if (args[0] is "--version" or "version")
            {
                await stdout.WriteLineAsync(typeof(Cli).Assembly.GetName().Version?.ToString() ?? "0").ConfigureAwait(false);
                return ExitCode.Passed;
            }

            if (args[0] == "db")
            {
                if (args.Length < 2 || args[1] is not ("provision" or "migrate"))
                {
                    await stderr.WriteLineAsync("db supports: provision, and migrate").ConfigureAwait(false);
                    return ExitCode.Usage;
                }

                try
                {
                    return args[1] == "provision"
                        ? await DbCommands.ProvisionAsync(DbProvisionArguments.Parse(args[2..]), stdout, stderr, cancellationToken).ConfigureAwait(false)
                        : await DbCommands.MigrateAsync(DbMigrateArguments.Parse(args[2..]), stdout, stderr, cancellationToken).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
                    return ExitCode.Usage;
                }
            }

            if (args[0] == "audit")
            {
                if (args.Length >= 2 && args[1] == "export")
                {
                    AuditExportArguments exportArguments;
                    try
                    {
                        exportArguments = AuditExportArguments.Parse(args[2..]);
                    }
                    catch (ArgumentException ex)
                    {
                        await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
                        return ExitCode.Usage;
                    }

                    return await AuditExportCommand.RunAsync(exportArguments, stdout, stderr, cancellationToken).ConfigureAwait(false);
                }

                if (args.Length < 2 || args[1] != "verify")
                {
                    await stderr.WriteLineAsync("audit supports: export --server <https://host> --namespace <guid> --reason \"<why>\" (--from/--to/--run), and verify <bundle> --key <public key file>").ConfigureAwait(false);
                    return ExitCode.Usage;
                }

                AuditVerifyArguments audit;
                try
                {
                    audit = AuditVerifyArguments.Parse(args[2..]);
                }
                catch (ArgumentException ex)
                {
                    await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
                    return ExitCode.Usage;
                }

                return await AuditVerifyCommand.RunAsync(audit, stdout, stderr, cancellationToken).ConfigureAwait(false);
            }

            if (args[0] == "history")
            {
                if (args.Length == 3 && args[1] == "verify")
                {
                    return await HistoryVerifyCommand.RunAsync(args[2], stdout, stderr, cancellationToken).ConfigureAwait(false);
                }

                if (args.Length < 2 || args[1] != "export")
                {
                    await stderr.WriteLineAsync("history supports: export <run-id> --reason \"<why>\", and verify <file>").ConfigureAwait(false);
                    return ExitCode.Usage;
                }

                HistoryExportArguments export;
                try
                {
                    export = HistoryExportArguments.Parse(args[2..]);
                }
                catch (ArgumentException ex)
                {
                    await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
                    return ExitCode.Usage;
                }

                return await HistoryExportCommand.RunAsync(export, store: null, TimeProvider.System, stdout, stderr, cancellationToken).ConfigureAwait(false);
            }

            if (args[0] != "replay-check")
            {
                await stderr.WriteLineAsync($"Unknown command '{args[0]}'.").ConfigureAwait(false);
                await stderr.WriteLineAsync(Usage).ConfigureAwait(false);
                return ExitCode.Usage;
            }

            ReplayCheckArguments parsed;
            try
            {
                parsed = ReplayCheckArguments.Parse(args[1..], DateTimeOffset.UtcNow);
            }
            catch (ArgumentException ex)
            {
                await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
                return ExitCode.Usage;
            }

            return await ReplayCheckCommand.RunAsync(parsed, store: null, stdout, stderr, cancellationToken).ConfigureAwait(false);
        }
    }
}
