using Kairos.Cli;

return await Cli.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);

namespace Kairos.Cli
{
    /// <summary>The <c>kairos</c> command.</summary>
    internal static class Cli
    {
        private const string Usage = """
            kairos: Kairos command line

              kairos replay-check --build <assembly> [--connection <string> | set KAIROS_CONNECTION] [--corpus <dir>]
                                  [--tenant <guid> --namespace <guid>] [--sample <n|all>] [--since <30m|12h|7d|date>]
                                  [--strict-coverage] [--format text|json] [--setup <type>]

              kairos history export <run-id> --reason "<why>" [--out <file>] [--metadata-only] [--actor <name>] [--force]
                                    [--connection <string> | set KAIROS_CONNECTION] [--tenant <guid> --namespace <guid>]

              kairos history verify <file>

              kairos audit verify <bundle> --key <public key file> [--kid <key id>] [--history <file>]... [--require-opened]

            audit verify checks a signed audit evidence bundle offline: the signature under the public key you supply (the key of the customer who signed, from their key service), the
            evidence against the signed manifest, every record's link, and the chain heads. Each --history file (from history export, full or metadata only) is checked against the recorded
            head of its run. It prints what it checked, never a record's contents. Exit codes: 0 verified, 1 not verified, 2 could not run.

            history verify checks a history file (the hash chain, or the digest chain of a metadata-only file) and prints its head hash.

            history export copies one run's history to a file (ciphertext as stored) for local replay under a debugger. Operators only, a reason is required, and the
            export is written to the audit log with your name and the reason.

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

            if (args[0] == "audit")
            {
                if (args.Length < 2 || args[1] != "verify")
                {
                    await stderr.WriteLineAsync("audit supports: verify <bundle> --key <public key file>").ConfigureAwait(false);
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
