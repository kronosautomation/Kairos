using System.Text;
using Kairos;
using Kairos.Testing;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>Shows replaying an exported run on a developer machine, and reading what a run's model steps used.</summary>
public sealed class ReplayLocallyTests : IDisposable
{
    private static readonly TenantScope Scope = new(Guid.Parse("99999999-0000-0000-0000-000000000001"), Guid.Parse("99999999-0000-0000-0000-0000000000a1"));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairos-sample-" + Guid.NewGuid().ToString("N"));

    public ReplayLocallyTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static async Task<byte[]> Ship(IWorkflowContext ctx, byte[] input)
    {
        byte[] receipt = await ctx.ExecuteActivityAsync("charge", input);
        await ctx.DelayAsync(TimeSpan.FromSeconds(5));
        return receipt;
    }

    [Fact]
    public async Task An_exported_run_replays_locally_and_a_changed_workflow_is_reported_at_the_first_difference()
    {
        var engine = new TestEngine();
        engine.RegisterWorkflow("ship", Ship);
        engine.RegisterActivity("charge", input => Task.FromResult(input));
        Guid id = engine.StartWorkflow("ship", Encoding.UTF8.GetBytes("order-1"), Scope.TenantId, Scope.NamespaceId);
        await engine.RunAsync(id);
        var operatorPrincipal = new Principal(Scope, Role.Operator, "dev@example.com", null);
        HistoryExport export = (await HistoryExporter.ExportAsync(engine.Store, operatorPrincipal, new RunKey(Scope.TenantId, Scope.NamespaceId, id), "reproduce ticket 4411", false, engine.Time))!;
        string file = Path.Combine(_dir, "run.khist"); // kairos history export <run> --reason "reproduce ticket 4411" writes this file
        await File.WriteAllTextAsync(file, export.FileText);

        #region replay-file
        // Your workflow code runs here, in your process: set a breakpoint in it and step through the recorded run. Activities do not run; their recorded results are fed back.
        // Pass a protector built with your own identity if the application seals payloads (the file holds ciphertext only).
        ReplayOutcome outcome = await KairosReplayer.ReplayAsync(file, Ship);
        if (!outcome.Succeeded)
        {
            Console.WriteLine(outcome.Text); // the same report as replay-check: the first event the code no longer matches
        }
        #endregion

        Assert.True(outcome.Succeeded);
        ReplayOutcome changed = await KairosReplayer.ReplayAsync(file, async (ctx, input) =>
        {
            await ctx.DelayAsync(TimeSpan.FromSeconds(5));
            return await ctx.ExecuteActivityAsync("charge", input);
        });
        Assert.Contains("first divergence at event", changed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_reports_what_its_model_steps_used()
    {
        var store = new InMemoryKairosStore();
        var clock = new VirtualTimeProvider();
        var kairos = new KairosClient(store, clock);
        var workflows = new WorkflowRegistry().Register("agent", "default", async (ctx, input) => await ctx.ExecuteActivityAsync("model", input));
        var activities = new ActivityRegistry().Register("model", (context, input, _) =>
        {
            context.ReportUsage(new UsageRecord(InputTokens: 1200, OutputTokens: 340, CachedInputTokens: 800, Model: "model-2026-10"));
            return Task.FromResult(input);
        });
        StartRunResult start = await kairos.StartAsync(Scope, "agent-1", "agent", [1]);
        await Driver.DriveAsync(new WorkerRuntime(store, Scope, workflows, activities, clock, new WorkerOptions { AutoHeartbeat = false }));
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);

        #region run-usage
        // Tokens recorded once per model step, in the step's own history event. Readable without a key, and never counted again by a replay.
        RunUsage usage = await kairos.GetUsageAsync(run);
        Console.WriteLine($"{usage.Steps} model steps, {usage.InputTokens} in ({usage.CachedInputTokens} cached), {usage.OutputTokens} out");
        foreach (ModelUsage model in usage.ByModel)
        {
            Console.WriteLine($"  {model.Model}: {model.TotalTokens} tokens over {model.Steps} steps");
        }
        #endregion

        Assert.Equal((1, 1200L, 340L, 800L), (usage.Steps, usage.InputTokens, usage.OutputTokens, usage.CachedInputTokens));
    }
}
