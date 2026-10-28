using System.Text;
using Kairos.Agents;
using Kairos.Testing;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Persistence.Tests;

/// <summary>The agent layer on the real store: model and tool steps, an approval by a named person, and a saga rollback, in an embedded host.</summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class AgentsOnPostgresTests(PostgresFixture fixture)
{
    private static readonly byte[] Empty = [];
    private static readonly string[] Effectful = ["book", "cancel"];

    private static async Task<RunState> WaitForCloseAsync(KairosClient client, RunKey run)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            RunState? state = await client.GetStateAsync(run);
            if (state is RunState.Completed or RunState.Failed)
            {
                return state.Value;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Run did not close within 60 s.");
    }

    private static async Task<IReadOnlyList<PendingApproval>> WaitForApprovalAsync(KairosClient client, RunKey run)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            IReadOnlyList<PendingApproval> pending = AgentHistory.PendingApprovals(await client.GetOpenedHistoryAsync(run));
            if (pending.Count > 0)
            {
                return pending;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("No approval was requested within 60 s.");
    }

    private static IHost Host(string connectionString, AgentsRuntime agents, FakeExternalSystem external)
    {
        HostApplicationBuilder builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.AllowUnencryptedPayloads();
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            o.UsePostgres(connectionString).AddActivities(agents.Activities);
            foreach (string name in Effectful)
            {
                o.AddActivity(name, (ctx, _, _) =>
                {
                    external.Apply(ctx.IdempotencyKey);
                    return Task.FromResult(new byte[] { 1 });
                });
            }

            o.AddActivity("charge", (_, _, _) => throw new InvalidOperationException("card declined"));
            o.AddWorkflow("trip", agents.Workflow(async (ctx, _) =>
            {
                await ctx.RunSagaAsync(async saga =>
                {
                    await ctx.Workflow.ExecuteActivityAsync("book", Empty);
                    saga.Compensate("book", "cancel", Empty);
                    (await ctx.RequestApprovalAsync(new ApprovalRequest { Action = "Charge the card" })).ThrowIfNotApproved();
                    await ctx.Workflow.ExecuteActivityAsync("charge", Empty, RetryPolicy.NoRetry);
                });
                return Empty;
            }));
            o.AddWorkflow("support", agents.Workflow(async (ctx, input) =>
                Encoding.UTF8.GetBytes(await AgentLoopAsync(ctx, Encoding.UTF8.GetString(input)))));
        });
        return builder.Build();
    }

    private static async Task<string> AgentLoopAsync(IAgentContext ctx, string text)
    {
        var conversation = new List<ChatMessage> { new(ChatRole.User, text) };
        AgentToolSet tools = ctx.Tools("lookup");
        while (true)
        {
            AgentReply reply = await ctx.ChatAsync("m", conversation, tools);
            conversation.AddRange(reply.Messages);
            if (reply.ToolCalls.Count == 0)
            {
                return reply.Text;
            }

            foreach (FunctionCallContent call in reply.ToolCalls)
            {
                conversation.Add(await ctx.InvokeToolAsync(call));
            }
        }
    }

    private static AgentsRuntime Agents(ScriptedChatClient model) => new AgentsBuilder()
        .AllowUnverifiedApprovers()
        .AddModel("m", model)
        .AddTool(new AgentTool("lookup", "Finds an order", """{"type":"object","properties":{}}""", ToolAccess.ReadOnly, (i, _) => ValueTask.FromResult("shipped " + i.IdempotencyKey[..8])))
        .Build();

    [Fact]
    public async Task An_agent_run_with_a_tool_completes_on_postgres_and_the_model_is_called_once_per_step()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        ScriptedChatClient model = AgentHarness_CallsToolThenAnswers();
        using IHost host = Host(db.DataPlaneConnectionString, Agents(model), new FakeExternalSystem());
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "support-1", "support", Encoding.UTF8.GetBytes("where is my order"));
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);

        Assert.Equal(RunState.Completed, await WaitForCloseAsync(client, run));
        IReadOnlyList<HistoryEvent> history = await client.GetOpenedHistoryAsync(run);
        HashChain.Verify(history);
        Assert.Equal("all done", Encoding.UTF8.GetString(((WorkflowCompleted)history[^1]).Result));
        Assert.Equal(2, model.CallCount);
        Assert.Equal(new AgentUsage(20, 10, 30), AgentHistory.TotalUsage(history));
        await host.StopAsync();
    }

    private static ScriptedChatClient AgentHarness_CallsToolThenAnswers() => new(messages =>
        messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
            ? new ChatMessage(ChatRole.Assistant, "all done")
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]));

    [Fact]
    public async Task A_rejected_approval_rolls_the_saga_back_on_postgres_exactly_once()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        var external = new FakeExternalSystem();
        using IHost host = Host(db.DataPlaneConnectionString, Agents(AgentHarness_CallsToolThenAnswers()), external);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "trip-1", "trip", Empty);
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);

        PendingApproval pending = Assert.Single(await WaitForApprovalAsync(client, run));
        var agentClient = new AgentClient(client, TimeProvider.System);
        Assert.True(await agentClient.RejectAsync(run, pending.ApprovalId, new ApproverIdentity("user:alice", "Alice")));

        Assert.Equal(RunState.Failed, await WaitForCloseAsync(client, run));
        IReadOnlyList<HistoryEvent> history = await client.GetOpenedHistoryAsync(run);
        HashChain.Verify(history);
        Assert.Null(SagaHistory.CheckCompensationsComplete(history));
        SagaRollback rollback = Assert.Single(SagaHistory.Rollbacks(history));
        Assert.Equal(["book"], rollback.Finished);
        Assert.Equal(2, external.Effects.Count); // booked once, cancelled once
        Assert.Equal(nameof(ApprovalRejectedException), ((WorkflowFailed)history[^1]).ErrorType);
        await host.StopAsync();
    }

    [Fact]
    public async Task A_failure_after_an_approval_also_rolls_back_and_the_approver_is_in_the_stored_history()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        var external = new FakeExternalSystem();
        using IHost host = Host(db.DataPlaneConnectionString, Agents(AgentHarness_CallsToolThenAnswers()), external);
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "trip-2", "trip", Empty);
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);

        PendingApproval pending = Assert.Single(await WaitForApprovalAsync(client, run));
        await new AgentClient(client, TimeProvider.System).ApproveAsync(run, pending.ApprovalId, new ApproverIdentity("user:alice", "Alice"), "go");

        Assert.Equal(RunState.Failed, await WaitForCloseAsync(client, run)); // the card is declined
        IReadOnlyList<HistoryEvent> history = await client.GetOpenedHistoryAsync(run);
        Assert.Null(SagaHistory.CheckCompensationsComplete(history));
        Assert.Contains("user:alice", Encoding.UTF8.GetString(history.OfType<SignalReceived>().Single().Payload), StringComparison.Ordinal);
        Assert.Equal(2, external.Effects.Count);
        await host.StopAsync();
    }
}
