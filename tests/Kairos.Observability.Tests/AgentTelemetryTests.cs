using System.Diagnostics;
using System.Text;
using Kairos.Agents;
using Kairos.Agents.Tests;
using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;

namespace Kairos.Observability.Tests;

[Collection(TelemetryTests.Name)]
public sealed class AgentTelemetryTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    private static AgentHarness SupportAgent(ScriptedChatClient model) => new(b =>
    {
        b.AddModel("support-model", model);
        b.AddTool(AgentHarness.ReadTool("lookup", _ => $"shipped {Canary.Text}"));
    });

    [Fact]
    public async Task A_model_step_and_a_tool_step_follow_the_genai_conventions_inside_the_activity_attempt()
    {
        using var telemetry = new Collector();
        AgentHarness h = SupportAgent(AgentHarness.CallsToolThenAnswers("lookup", $$"""{"order":"{{Canary.Text}}"}""", answer: $"It left today. {Canary.Text}"));
        h.Workflow("support", async (ctx, input) =>
        {
            ctx.OnBehalfOf = $"user:{Canary.Text}";
            return AgentHarness.Bytes(await AgentHarness.AgentLoopAsync(ctx, "support-model", $"where is {Canary.Text}", "lookup"));
        });

        RunKey run = await h.StartAsync("support");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Activity[] chats = [.. telemetry.Spans.Where(s => s.OperationName == "chat support-model")];
        Assert.Equal(2, chats.Length);
        Activity chat = chats[0];
        Assert.Equal(ActivityKind.Client, chat.Kind);
        Assert.Equal("chat", chat.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("support-model", chat.GetTagItem("gen_ai.request.model"));
        Assert.Equal(10L, chat.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(5L, chat.GetTagItem("gen_ai.usage.output_tokens"));
        Assert.Equal(run.RunId.ToString("D"), chat.GetTagItem("gen_ai.conversation.id"));
        Assert.NotNull(chat.GetTagItem("gen_ai.response.finish_reasons"));
        Assert.Contains(telemetry.Named("kairos.activity.attempt"), a => a.SpanId == chat.ParentSpanId); // inside the attempt that ran the step

        Activity tool = Assert.Single(telemetry.Named("execute_tool lookup"));
        Assert.Equal("execute_tool", tool.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("lookup", tool.GetTagItem("gen_ai.tool.name"));
        Assert.Equal("call-1", tool.GetTagItem("gen_ai.tool.call.id"));
        Assert.Equal("completed", tool.GetTagItem("kairos.agent.tool.outcome"));

        Assert.True(telemetry.Sum("gen_ai.client.token.usage", p => (string?)p.Tags["gen_ai.token.type"] == "input") >= 20);
        Assert.Contains(telemetry.Points, p => p.Instrument == "gen_ai.client.operation.duration" && (string?)p.Tags["gen_ai.operation.name"] == "execute_tool");

        // Content capture is off, and there is no switch to turn it on: not the prompt, the reply, the tool arguments, the tool result or who the agent acts for.
        Assert.Empty(Canary.FoundIn(telemetry.Everything()));
        Assert.DoesNotContain(telemetry.Spans.SelectMany(s => s.Tags.Select(t => t.Key)), k => k.StartsWith("gen_ai.input", StringComparison.Ordinal) || k.StartsWith("gen_ai.output", StringComparison.Ordinal) ||
            k.StartsWith("gen_ai.tool.call.arguments", StringComparison.Ordinal) || k.StartsWith("gen_ai.tool.call.result", StringComparison.Ordinal) || k.Contains("system_instructions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_model_call_records_the_exception_type_and_never_its_message()
    {
        using var telemetry = new Collector();
        var failing = new ScriptedChatClient(_ => throw new InvalidOperationException($"the model said no to {Canary.Text}"));
        var h = new AgentHarness(b => b.AddModel("m", failing).WithModelRetry(RetryPolicy.NoRetry));
        h.Workflow("chat", async (ctx, _) => AgentHarness.Bytes((await ctx.ChatAsync("m", [new ChatMessage(ChatRole.User, "hi")])).Text));

        RunKey run = await h.StartAsync("chat");
        Assert.Equal(RunState.Failed, await h.RunAsync(run));

        Activity chat = Assert.Single(telemetry.Spans, s => s.OperationName == "chat m");
        Assert.Equal(ActivityStatusCode.Error, chat.Status);
        Assert.Equal("InvalidOperationException", chat.GetTagItem(KairosTags.ErrorType));
        Assert.Empty(Canary.FoundIn(telemetry.Everything()));
    }

    [Fact]
    public async Task An_approval_shows_up_as_the_decision_the_person_sent_linked_to_the_run()
    {
        using var telemetry = new Collector();
        var h = new AgentHarness(b =>
        {
            b.AddModel("m", AgentHarness.Echo());
            b.AddTool(AgentHarness.WriteTool("refund", _ => "refunded"));
            b.ConfigureBaselinePolicy(p => p.RequireApproval("refund", new ApprovalRequirement(Roles: ["finance"])));
        });
        h.Workflow("call", async (ctx, _) =>
        {
            ToolStepResult r = await ctx.InvokeToolWithOutcomeAsync(new FunctionCallContent("c1", "refund", AgentHarness.WireArgs("{}")));
            return Encoding.UTF8.GetBytes(r.Outcome.ToString());
        });
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);
        string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;

        Assert.True(await h.Agent.ApproveAsync(run, id, Alice, $"fine, {Canary.Text}"));
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        Activity decide = Assert.Single(telemetry.Named("kairos.approval.decide"));
        Assert.Equal(id, decide.GetTagItem(KairosTags.ApprovalId));
        Activity approval = Assert.Single(telemetry.Named("kairos.approval"));
        Assert.Equal(id, approval.GetTagItem(KairosTags.ApprovalId));
        Assert.Equal(decide.SpanId, Assert.Single(approval.Links).Context.SpanId);
        Assert.Equal(1, telemetry.Sum("kairos.signals.received", p => (string?)p.Tags["kind"] == "approval"));
        Assert.Empty(Canary.FoundIn(telemetry.Everything())); // not the comment, not the approver's name
        Assert.DoesNotContain("Alice", telemetry.Everything(), StringComparison.Ordinal);
    }
}
