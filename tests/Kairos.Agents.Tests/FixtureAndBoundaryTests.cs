using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Testing.Agents;
using Kairos.Worker;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

public sealed class FixtureTests
{
    private static List<ChatMessage> User(string text) => [new ChatMessage(ChatRole.User, text)];

    private static async Task<byte[]> SupportWorkflow(IAgentContext ctx) =>
        AgentHarness.Bytes(await AgentHarness.AgentLoopAsync(ctx, "m", "where is my order", "lookup"));

    [Fact]
    public async Task A_recorded_conversation_replays_without_a_model_and_survives_a_file_round_trip()
    {
        // Record once against a stand-in for the real model.
        var recording = new ModelFixture();
        var real = new ScriptedChatClient(m => m.Any(x => x.Contents.OfType<FunctionResultContent>().Any())
            ? new ChatMessage(ChatRole.Assistant, "It left the warehouse today.")
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", AgentHarness.WireArgs("""{"order":"8841"}"""))]));
        var recorder = new RecordingChatClient(real, recording);
        var first = new AgentHarness(b => b.AddModel("m", recorder).AddTool(AgentHarness.ReadTool("lookup", _ => "shipped")));
        first.Workflow("support", async (ctx, _) => await SupportWorkflow(ctx));
        RunKey run1 = await first.StartAsync("support");
        Assert.Equal(RunState.Completed, await first.RunAsync(run1));
        Assert.Equal(2, recording.Count);

        string path = Path.Combine(Path.GetTempPath(), $"kairos-fixture-{Guid.NewGuid():N}.json");
        try
        {
            recording.Save(path);
            ModelFixture loaded = ModelFixture.Load(path);
            var fixtureClient = new FixtureChatClient(loaded);
            var second = new AgentHarness(b => b.AddModel("m", fixtureClient).AddTool(AgentHarness.ReadTool("lookup", _ => "shipped")));
            second.Workflow("support", async (ctx, _) => await SupportWorkflow(ctx));
            RunKey run2 = await second.StartAsync("support");

            Assert.Equal(RunState.Completed, await second.RunAsync(run2));
            Assert.Equal("It left the warehouse today.", await second.ResultTextAsync(run2));
            Assert.Equal(2, fixtureClient.CallCount); // two model steps; no real model anywhere
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_conversation_with_no_recording_fails_loudly_instead_of_calling_anything()
    {
        var client = new FixtureChatClient(new ModelFixture());
        var h = new AgentHarness(b => b.AddModel("m", client).WithModelRetry(RetryPolicy.NoRetry));
        h.Workflow("chat", async (ctx, _) => AgentHarness.Bytes((await ctx.ChatAsync("m", User("never recorded"))).Text));

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));
        Assert.Equal(nameof(FixtureMissException), (await h.HistoryAsync(run)).OfType<ActivityFailed>().Single().ErrorType);
    }

    [Fact]
    public async Task A_recorded_rate_limit_is_retried_and_then_the_reply_arrives()
    {
        List<ChatMessage> request = User("hi");
        ModelFixture fixture = new ModelFixture()
            .AddError(request, FixtureErrorKind.RateLimited, "429")
            .AddError(request, FixtureErrorKind.Timeout)
            .AddReply(request, new ChatMessage(ChatRole.Assistant, "finally"), inputTokens: 5, outputTokens: 2);
        var client = new FixtureChatClient(fixture);
        var h = new AgentHarness(b => b.AddModel("m", client));
        h.Workflow("chat", async (ctx, _) => AgentHarness.Bytes((await ctx.ChatAsync("m", request)).Text));

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Assert.Equal("finally", await h.ResultTextAsync(run));
        Assert.Equal(3, client.CallCount);
        string[] errors = [.. (await h.HistoryAsync(run)).OfType<ActivityFailed>().Select(f => f.ErrorType)];
        Assert.Equal([nameof(ModelRateLimitedException), nameof(TimeoutException)], errors);
        Assert.Equal(new AgentUsage(5, 2, 7), AgentHistory.TotalUsage(await h.HistoryAsync(run)));
    }

    [Fact]
    public async Task A_malformed_provider_answer_exhausts_the_retries_and_fails_the_step()
    {
        List<ChatMessage> request = User("hi");
        var client = new FixtureChatClient(new ModelFixture().AddError(request, FixtureErrorKind.Malformed, "not json"));
        var h = new AgentHarness(b => b.AddModel("m", client));
        h.Workflow("chat", async (ctx, _) =>
        {
            try
            {
                await ctx.ChatAsync("m", request);
                return AgentHarness.Bytes("unexpected");
            }
            catch (ActivityFailureException ex)
            {
                return AgentHarness.Bytes("caught " + ex.ErrorType);
            }
        });

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal("caught JsonException", await h.ResultTextAsync(run));
        Assert.Equal(3, client.CallCount);
    }

    [Fact]
    public async Task A_refusal_is_just_a_reply_and_the_workflow_can_branch_on_it()
    {
        List<ChatMessage> request = User("do something dangerous");
        ModelFixture fixture = new ModelFixture().AddReply(request, new ChatMessage(ChatRole.Assistant, "I can't help with that."), finishReason: "content_filter");
        var h = new AgentHarness(b => b.AddModel("m", new FixtureChatClient(fixture)));
        h.Workflow("chat", async (ctx, _) =>
        {
            AgentReply reply = await ctx.ChatAsync("m", request);
            return AgentHarness.Bytes($"{reply.FinishReason}|{reply.ToolCalls.Count}");
        });

        RunKey run = await h.StartAsync("chat");
        await h.RunAsync(run);
        Assert.Equal("content_filter|0", await h.ResultTextAsync(run));
    }

    [Fact]
    public void Equal_conversations_have_equal_fingerprints_and_different_ones_do_not()
    {
        Assert.Equal(AgentMessageCodec.Fingerprint(User("a")), AgentMessageCodec.Fingerprint(User("a")));
        Assert.NotEqual(AgentMessageCodec.Fingerprint(User("a")), AgentMessageCodec.Fingerprint(User("b")));
        Assert.NotEqual(
            AgentMessageCodec.Fingerprint(User("a")),
            AgentMessageCodec.Fingerprint([new ChatMessage(ChatRole.Assistant, "a")]));
    }

    [Fact]
    public void A_conversation_survives_serialization_with_calls_and_results()
    {
        List<ChatMessage> conversation =
        [
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", AgentHarness.WireArgs("""{"id":7,"deep":{"a":[1,2]}}"""))]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "result text")]),
        ];

        IReadOnlyList<ChatMessage> back = AgentMessageCodec.Deserialize(AgentMessageCodec.Serialize(conversation));

        Assert.Equal(AgentMessageCodec.Fingerprint(conversation), AgentMessageCodec.Fingerprint(back));
        FunctionCallContent call = Assert.IsType<FunctionCallContent>(back[1].Contents[0]);
        Assert.Equal("lookup", call.Name);
        Assert.Equal(7, ((System.Text.Json.JsonElement)call.Arguments!["id"]!).GetInt32());
    }
}

public sealed class BoundaryTests
{
    [Fact]
    public void The_agent_package_depends_on_core_and_the_model_abstractions_only()
    {
        // docs: Kairos.Agents* depend on Core/Abstractions + Microsoft.Extensions.AI abstractions only; no model vendor SDK, no persistence, no cloud SDK.
        string[] allowedPrefixes = ["System", "Microsoft.Extensions.AI.Abstractions", "Microsoft.Extensions.Logging.Abstractions", "Microsoft.Extensions.DependencyInjection.Abstractions", "Kairos.Core", "Kairos.Abstractions", "netstandard", "mscorlib", "Microsoft.Bcl", "Microsoft.CSharp"];
        string[] referenced = [.. typeof(AgentsRuntime).Assembly.GetReferencedAssemblies().Select(a => a.Name!)];

        Assert.All(referenced, name => Assert.True(allowedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)), $"Kairos.Agents references {name}."));
        Assert.DoesNotContain(referenced, n => n.Contains("OpenAI", StringComparison.OrdinalIgnoreCase) || n.Contains("Anthropic", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Npgsql", StringComparison.Ordinal) || n.StartsWith("Kairos.Persistence", StringComparison.Ordinal) || n.StartsWith("Kairos.Cloud", StringComparison.Ordinal));
    }

    [Fact]
    public void The_agent_package_has_no_reflection_based_json()
    {
        // System.Text.Json source generators only: no JsonSerializer call without type info. (The AOT analyzers also guard this at build time.)
        string source = File.ReadAllText(FindFile("src/Kairos.Agents/AgentContext.cs")) + File.ReadAllText(FindFile("src/Kairos.Agents/AgentsRuntime.cs"));
        Assert.DoesNotContain("JsonSerializer.Serialize<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize<", source, StringComparison.Ordinal);
    }

    private static string FindFile(string relative)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Kairos.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return Path.Combine(dir ?? throw new FileNotFoundException("Kairos.sln"), relative);
    }

    [Fact]
    public async Task With_client_side_encryption_the_server_never_holds_prompts_tool_arguments_or_results()
    {
        var protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
        const string Canary = "CANARY-4111-secret";
        var model = new ScriptedChatClient(m => m.Any(x => x.Contents.OfType<FunctionResultContent>().Any())
            ? new ChatMessage(ChatRole.Assistant, "answer " + Canary)
            : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", AgentHarness.WireArgs($$"""{"card":"{{Canary}}"}"""))]));
        string? seenArgs = null;
        var h = new AgentHarness(
            b => b.AddModel("m", model).AddTool(AgentHarness.ReadTool("lookup", i =>
            {
                seenArgs = i.Arguments.GetRawText();
                return "result " + Canary;
            })),
            protector: protector);
        h.Workflow("support", async (ctx, input) => AgentHarness.Bytes(await AgentHarness.AgentLoopAsync(ctx, "m", AgentHarness.Text(input), "lookup")));

        RunKey run = await h.StartAsync("support", "my card is " + Canary);
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        // The worker (the customer) saw plaintext everywhere it must...
        Assert.Contains(Canary, seenArgs, StringComparison.Ordinal);
        Assert.Equal("answer " + Canary, await h.ResultTextAsync(run));

        // ...and the server's copy of the history holds none of it.
        foreach (HistoryEvent e in await h.Store.GetHistoryAsync(run, CancellationToken.None))
        {
            string raw = e switch
            {
                ActivityScheduled s => Encoding.UTF8.GetString(s.Input),
                ActivityCompleted c => Encoding.UTF8.GetString(c.Result),
                WorkflowStarted w => Encoding.UTF8.GetString(w.Input),
                WorkflowCompleted w => Encoding.UTF8.GetString(w.Result),
                SignalReceived sig => Encoding.UTF8.GetString(sig.Payload),
                _ => "",
            };
            Assert.DoesNotContain(Canary, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Canary)), raw, StringComparison.Ordinal);
        }

    }

    [Fact]
    public async Task Approvals_also_work_under_client_side_encryption()
    {
        var protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());
        var h = new AgentHarness(
            b => b.AddModel("m", AgentHarness.Echo()).AddTool(AgentHarness.WriteTool("refund", _ => "refunded")),
            protector: protector);
        h.Workflow("call", async (ctx, _) =>
        {
            ToolStepResult r = await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c1", "refund", null));
            return AgentHarness.Bytes(r.Outcome.ToString());
        });
        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Running, await h.RunAsync(run));
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        await h.Agent.ApproveAsync(run, id, new ApproverIdentity("user:alice", "Alice", ["finance"]));

        Assert.Equal(RunState.Completed, await h.RunAsync(run));
        Assert.Equal("Completed", await h.ResultTextAsync(run));
    }
}
