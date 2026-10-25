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
