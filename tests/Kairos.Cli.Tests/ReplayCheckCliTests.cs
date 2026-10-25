using System.Reflection;
using Kairos;
using System.Text;
using Kairos.Cli;
using Kairos.Testing;

[assembly: ReplayCheckSetup(typeof(Kairos.Cli.Tests.UnchangedBuild))]

namespace Kairos.Cli.Tests;

/// <summary>The build as it was when the histories were recorded.</summary>
public sealed class UnchangedBuild : IReplayCheckSetup
{
    public void Configure(KairosOptions options) => options.AddWorkflow("order", Orders.Original);
}

/// <summary>A new build in which a step moved: it would diverge from every recorded run.</summary>
public sealed class ReorderedBuild : IReplayCheckSetup
{
    public void Configure(KairosOptions options) => options.AddWorkflow("order", async (ctx, input) =>
    {
        await ctx.DelayAsync(TimeSpan.FromSeconds(5));
        return await ctx.ExecuteActivityAsync("charge", input);
    });
}

/// <summary>A build that carries only a different build id, so it covers none of the recorded runs.</summary>
public sealed class OtherBuild : IReplayCheckSetup
{
    public void Configure(KairosOptions options) => options.AddWorkflow("order", Orders.Original, buildId: "2026.11");
}

/// <summary>Not a setup at all.</summary>
public sealed class NotASetup
{
}

internal static class Orders
{
    public const string Canary = "CANARY-5e0c-never-printed";

    public static async Task<byte[]> Original(IWorkflowContext ctx, byte[] input)
    {
        byte[] receipt = await ctx.ExecuteActivityAsync("charge", input);
        await ctx.DelayAsync(TimeSpan.FromSeconds(5));
        return receipt;
    }
}

public sealed class ReplayCheckCliTests
{
    private static readonly string Build = typeof(UnchangedBuild).Assembly.Location;
    private static readonly TenantScope Scope = new(Guid.Parse("22222222-0000-0000-0000-000000000001"), Guid.Parse("22222222-0000-0000-0000-0000000000a1"));

    private static async Task<(TestEngine Engine, Guid Run)> RecordAsync()
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("order", Orders.Original);
        engine.RegisterActivity("charge", input => Task.FromResult(input));
        Guid run = engine.StartWorkflow("order", Encoding.UTF8.GetBytes(Orders.Canary), Scope.TenantId, Scope.NamespaceId);
        await engine.RunAsync(run);
        return (engine, run);
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(IKairosStore? store, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        ReplayCheckArguments parsed = ReplayCheckArguments.Parse([.. args, "--tenant", Scope.TenantId.ToString(), "--namespace", Scope.NamespaceId.ToString()], DateTimeOffset.UtcNow);
        int exit = await ReplayCheckCommand.RunAsync(parsed, store, stdout, stderr, default);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task An_unchanged_build_passes_with_exit_code_zero()
    {
        (TestEngine engine, _) = await RecordAsync();

        (int exit, string output, _) = await RunAsync(engine.Store, "--build", Build);

        Assert.Equal(ExitCode.Passed, exit);
        Assert.Contains("PASSED", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_that_diverges_fails_with_the_workflow_run_and_first_diverging_command_and_no_payload()
    {
        (TestEngine engine, Guid run) = await RecordAsync();

        (int exit, string output, string error) = await RunAsync(engine.Store, "--build", Build, "--setup", typeof(ReorderedBuild).FullName!);

        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("FAILED", output, StringComparison.Ordinal);
        Assert.Contains("order", output, StringComparison.Ordinal);
        Assert.Contains($"run:{run:D}", output, StringComparison.Ordinal);
        Assert.Contains("first divergence at event", output, StringComparison.Ordinal);
        Assert.Contains("recorded: ActivityScheduled: activity 1 'charge'", output, StringComparison.Ordinal);
        Assert.Contains("emitted:  StartTimer: timer 1 for 00:00:05", output, StringComparison.Ordinal);
        Assert.DoesNotContain(Orders.Canary, output + error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_json_form_carries_the_same_verdict()
    {
        (TestEngine engine, _) = await RecordAsync();

        (int exit, string output, _) = await RunAsync(engine.Store, "--build", Build, "--setup", typeof(ReorderedBuild).FullName!, "--format", "json");

        Assert.Equal(ExitCode.Failed, exit);
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        Assert.False(doc.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.DoesNotContain(Orders.Canary, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_build_that_covers_none_of_the_recorded_runs_does_not_pass_and_strict_coverage_names_the_gap()
    {
        (TestEngine engine, _) = await RecordAsync();

        (int exit, string output, _) = await RunAsync(engine.Store, "--build", Build, "--setup", typeof(OtherBuild).FullName!, "--strict-coverage");

        Assert.Equal(ExitCode.Failed, exit);
        Assert.Contains("Nothing was replayed", output, StringComparison.Ordinal);
        Assert.Contains("not in the candidate", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Histories_in_files_are_replayed_and_a_broken_file_fails_the_check()
    {
        (TestEngine engine, Guid run) = await RecordAsync();
        string dir = Path.Combine(Path.GetTempPath(), "kairos-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "order-1.khist"), HistoryFile.Serialize(engine.GetHistory(run), "test", "recorded for the test"));

            (int ok, string passed, _) = await RunAsync(null, "--build", Build, "--corpus", dir);
            Assert.Equal(ExitCode.Passed, ok);
            Assert.Contains("PASSED", passed, StringComparison.Ordinal);

            (int bad, string failed, _) = await RunAsync(null, "--build", Build, "--setup", typeof(ReorderedBuild).FullName!, "--corpus", dir);
            Assert.Equal(ExitCode.Failed, bad);
            Assert.Contains("file:order-1.khist", failed, StringComparison.Ordinal);

            await File.WriteAllTextAsync(Path.Combine(dir, "broken.khist"), "event: not-base64 x y");
            (int broken, string brokenOutput, _) = await RunAsync(null, "--build", Build, "--corpus", dir);
            Assert.Equal(ExitCode.Failed, broken);
            Assert.Contains("not a valid history file", brokenOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Things_that_stop_the_check_from_running_are_exit_code_two_with_a_message()
    {
        (TestEngine engine, _) = await RecordAsync();

        (int missing, _, string missingError) = await RunAsync(engine.Store, "--build", "does-not-exist.dll");
        Assert.Equal(ExitCode.Usage, missing);
        Assert.Contains("does not exist", missingError, StringComparison.Ordinal);

        (int noSetup, _, string noSetupError) = await RunAsync(engine.Store, "--build", typeof(Assert).Assembly.Location);
        Assert.Equal(ExitCode.Usage, noSetup);
        Assert.Contains("names no replay-check setup", noSetupError, StringComparison.Ordinal);

        (int wrongType, _, string wrongTypeError) = await RunAsync(engine.Store, "--build", Build, "--setup", typeof(NotASetup).FullName!);
        Assert.Equal(ExitCode.Usage, wrongType);
        Assert.Contains("does not implement IReplayCheckSetup", wrongTypeError, StringComparison.Ordinal);

        (int noSource, _, string noSourceError) = await RunAsync(null, "--build", Build, "--connection-env", "KAIROS_SURELY_NOT_SET");
        Assert.Equal(ExitCode.Usage, noSource);
        Assert.Contains("Give the histories to replay", noSourceError, StringComparison.Ordinal);

        (int cannotList, _, string cannotListError) = await RunAsync(new NoListing(), "--build", Build);
        Assert.Equal(ExitCode.Usage, cannotList);
        Assert.Contains("cannot list runs", cannotListError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_payload_protector_is_a_warning_on_stderr()
    {
        (TestEngine engine, _) = await RecordAsync();

        (_, _, string error) = await RunAsync(engine.Store, "--build", Build);

        Assert.Contains("no payload protector", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_line_routes_commands_and_reports_usage_errors()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Type cli = typeof(ReplayCheckArguments).Assembly.GetType("Kairos.Cli.Cli")!;
        async Task<int> Run(params string[] args) =>
            await (Task<int>)cli.GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [args, stdout, stderr, CancellationToken.None])!;

        Assert.Equal(ExitCode.Usage, await Run());
        Assert.Equal(ExitCode.Passed, await Run("--help"));
        Assert.Contains("replay-check", stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCode.Usage, await Run("frobnicate"));
        Assert.Equal(ExitCode.Usage, await Run("replay-check"));
        Assert.Contains("--build", stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(ExitCode.Usage, await Run("replay-check", "--build", Build, "--tenant", Guid.NewGuid().ToString()));
        Assert.Equal(ExitCode.Usage, await Run("replay-check", "--build", Build, "--sample", "zero"));
    }

    [Theory]
    [InlineData("30m", 30 * 60)]
    [InlineData("12h", 12 * 3600)]
    [InlineData("7d", 7 * 86400)]
    public void Since_accepts_spans_back_from_now(string text, int seconds)
    {
        var now = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(now.AddSeconds(-seconds), ReplayCheckArguments.ParseSince(text, now));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), ReplayCheckArguments.ParseSince("2026-10-01", now));
        Assert.Throws<ArgumentException>(() => ReplayCheckArguments.ParseSince("yesterday-ish", now));
    }

    private sealed class NoListing : ForwardingStore
    {
        public NoListing()
            : base(new InMemoryKairosStore())
        {
        }

        public override ValueTask<IReadOnlyList<RunGroup>> GetRunGroupsAsync(TenantScope scope, DateTimeOffset? closedSince, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
