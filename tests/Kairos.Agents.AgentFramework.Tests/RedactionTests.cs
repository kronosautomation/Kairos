using System.Text;
using System.Text.Json;
using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>"Applied to every piece of text before it is recorded": for an Agent Framework agent that includes the text inside the session snapshot, which is Agent Framework's JSON, not Kairos'.</summary>
public sealed class RedactionTests
{
    private const string Secret = "SECRET-123-4567";
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    internal static string Redact(string text) => text.Replace(Secret, "[redacted]", StringComparison.Ordinal);

    /// <summary>All the bytes of a history that are text, as one string.</summary>
    internal static string Everything(IReadOnlyList<HistoryEvent> history)
    {
        var sb = new StringBuilder();
        foreach (HistoryEvent e in history)
        {
            switch (e)
            {
                case WorkflowStarted x: sb.AppendLine(Encoding.UTF8.GetString(x.Input)); break;
                case ActivityScheduled x: sb.AppendLine(x.ActivityType).AppendLine(Encoding.UTF8.GetString(x.Input)); break;
                case ActivityCompleted x: sb.AppendLine(Encoding.UTF8.GetString(x.Result)); break;
                case ActivityFailed x: sb.AppendLine(x.Message); break;
                case SignalReceived x: sb.AppendLine(x.SignalName).AppendLine(Encoding.UTF8.GetString(x.Payload)); break;
                case WorkflowCompleted x: sb.AppendLine(Encoding.UTF8.GetString(x.Result)); break;
                case WorkflowFailed x: sb.AppendLine(x.Message); break;
                case WorkflowContinued x: sb.AppendLine(Encoding.UTF8.GetString(x.Input)); break;
            }
        }

        return sb.ToString();
    }

    [Fact]
    public async Task Text_is_redacted_in_the_messages_the_tool_arguments_the_results_and_the_session_before_anything_is_recorded()
    {
        var seenByModel = new List<ChatMessage>();
        string? toolSaw = null;
        ScriptedChatClient model = new(messages =>
        {
            seenByModel.AddRange(messages);
            return messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
                ? new ChatMessage(ChatRole.Assistant, $"I found {Secret}.")
                : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", AfHarness.Args($$"""{"q":"{{Secret}}"}"""))]);
        });
        AIFunction lookup = AIFunctionFactory.Create((string q) => { toolSaw = q; return $"record for {Secret}"; }, "lookup", "d");
        var h = new AfHarness(af => af
            .ConfigureAgents(b => b.UseRedactor(Redact))
            .AddAgent(AfHarness.AgentWith(model, "agent", lookup), r => r.ReadOnly("lookup")));

        (AgentResponse? reply, DurableTurn turn) = await h.AskAsync("agent", "s", $"my number is {Secret}");

        Assert.Equal("I found [redacted].", AfHarness.Text(reply));
        Assert.Equal("[redacted]", toolSaw); // the tool gets what was recorded, as a native Kairos tool does
        Assert.DoesNotContain(seenByModel.SelectMany(m => m.Contents), c => (c is TextContent t && t.Text.Contains(Secret, StringComparison.Ordinal)) || (c is FunctionResultContent f && f.Result?.ToString()?.Contains(Secret, StringComparison.Ordinal) == true));
        string everything = Everything(await h.HistoryAsync(await h.RunOfAsync("agent", "s")));
        Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);
        Assert.Contains("[redacted]", everything, StringComparison.Ordinal);
        Assert.NotNull(turn);
    }

    [Fact]
    public async Task The_text_of_an_approval_request_is_redacted_too()
    {
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("issue_refund", $$"""{"q":"{{Secret}}"}""");
        AIFunction refund = AIFunctionFactory.Create((string q) => "refunded", "issue_refund", "d");
        var h = new AfHarness(af => af.ConfigureAgents(b => b.UseRedactor(Redact)).AddAgent(AfHarness.AgentWith(model, "agent", new ApprovalRequiredAIFunction(refund))));
        await h.AskAsync("agent", "s", "refund");
        RunKey run = await h.RunOfAsync("agent", "s");

        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(await h.HistoryAsync(run)));

        Assert.DoesNotContain(Secret, pending.Details, StringComparison.Ordinal);
        Assert.Contains("[redacted]", pending.Details, StringComparison.Ordinal);
        await h.Approvals.ApproveAsync(run, pending.ApprovalId, Alice);
        await h.DriveAsync();
        Assert.DoesNotContain(Secret, Everything(await h.HistoryAsync(run)), StringComparison.Ordinal);
    }

    // ---- the walker --------------------------------------------------------------------------------------------------

    private static async Task<JsonElement> SnapshotAsync()
    {
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("lookup", $$"""{"q":"{{Secret}}"}""", $"answer {Secret}");
        AIFunction lookup = AIFunctionFactory.Create((string q) => $"found {Secret}", "lookup", "d");
        ChatClientAgent agent = AfHarness.AgentWith(model, "agent", lookup);
        AgentSession session = await agent.CreateSessionAsync();
        await agent.RunAsync($"hello {Secret}", session);
        return await agent.SerializeSessionAsync(session);
    }

    [Fact]
    public async Task Only_the_values_of_the_members_that_carry_text_are_rewritten()
    {
        JsonElement snapshot = await SnapshotAsync();
        Assert.Contains(Secret, snapshot.GetRawText(), StringComparison.Ordinal);

        JsonElement redacted = SnapshotRedactor.RedactSnapshot(snapshot, Redact, new HashSet<string>());

        string text = redacted.GetRawText();
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.Equal(snapshot.GetRawText().Replace(Secret, "[redacted]", StringComparison.Ordinal), text); // nothing else moved: not a key, a role, an id or a type
    }

    [Theory]
    [InlineData("""{"stateBag":{"Unknown":{}}}""", "Unknown")]
    [InlineData("""{"stateBag":{},"surprise":1}""", "surprise")]
    [InlineData("""{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[],"extra":1}}}""", "extra")]
    [InlineData("""{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[{"role":"user","contents":[],"surprise":1}]}}}""", "surprise")]
    [InlineData("""{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[{"role":"user","contents":[{"$type":"hostedFile","fileId":"f"}]}]}}}""", "hostedFile")]
    [InlineData("""{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[{"role":"user","contents":[{"$type":"text","text":"x","annotations":[]}]}]}}}""", "annotations")]
    [InlineData("""[1,2]""", "not a JSON object")]
    public void A_shape_the_redactor_does_not_know_is_refused_not_skipped(string json, string named)
    {
        using JsonDocument doc = JsonDocument.Parse(json);

        SnapshotShapeException e = Assert.Throws<SnapshotShapeException>(() => SnapshotRedactor.RedactSnapshot(doc.RootElement, Redact, new HashSet<string>()));

        Assert.Contains(named, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void State_declared_free_of_text_passes_and_nothing_else_does()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"stateBag":{"Scope":{"user":"u-1"}}}""");

        JsonElement ok = SnapshotRedactor.RedactSnapshot(doc.RootElement, Redact, new HashSet<string> { "Scope" });

        Assert.Equal(doc.RootElement.GetRawText(), ok.GetRawText());
        Assert.Throws<SnapshotShapeException>(() => SnapshotRedactor.RedactSnapshot(doc.RootElement, Redact, new HashSet<string>()));
    }

    [Fact]
    public async Task A_pending_approval_in_the_session_is_redacted_like_the_message_it_came_from()
    {
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("issue_refund", $$"""{"q":"{{Secret}}"}""");
        AIFunction refund = AIFunctionFactory.Create((string q) => "refunded", "issue_refund", "d");
        ChatClientAgent agent = AfHarness.AgentWith(model, "agent", new ApprovalRequiredAIFunction(refund));
        AgentSession session = await agent.CreateSessionAsync();
        await agent.RunAsync("go", session);
        JsonElement snapshot = await agent.SerializeSessionAsync(session);
        Assert.Contains("_pendingApprovalRequests", snapshot.GetRawText(), StringComparison.Ordinal);

        JsonElement redacted = SnapshotRedactor.RedactSnapshot(snapshot, Redact, new HashSet<string>());

        Assert.DoesNotContain(Secret, redacted.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Messages_are_redacted_by_the_same_rules()
    {
        JsonElement messages = JsonSerializer.SerializeToElement(
            new List<ChatMessage> { new(ChatRole.User, $"hi {Secret}"), new(ChatRole.Tool, [new FunctionResultContent("c", JsonDocument.Parse("{\"a\":[\"" + Secret + "\",1,true,null],\"b\":{\"c\":\"" + Secret + "\"}}").RootElement.Clone())]) },
            AIJsonUtilities.DefaultOptions);

        JsonElement redacted = SnapshotRedactor.RedactMessages(messages, Redact);

        Assert.DoesNotContain(Secret, redacted.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("\"a\":[\"[redacted]\",1,true,null]", redacted.GetRawText(), StringComparison.Ordinal);
    }
}
