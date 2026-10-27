using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// What the adapter depends on in Agent Framework, written down as tests against the version it references. A re-pin that changes any of it fails here, in CI, not in production: re-pinning is a deliberate act that
/// starts by reading this file's failures. (docs/AGENT-FRAMEWORK-DESIGN.md section 2 names the pin.)
/// </summary>
public sealed class ContractTests
{
    [Fact]
    public void The_pinned_version_is_the_one_these_contracts_were_written_for()
    {
        string version = typeof(ChatClientAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith("1.24.0", version, StringComparison.Ordinal);
        Assert.StartsWith("1.24.0", typeof(AIAgent).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion, StringComparison.Ordinal);
    }

    // ---- how a run is cut into steps ---------------------------------------------------------------------------------

    [Fact]
    public async Task With_declarations_only_one_RunAsync_is_one_model_call_and_ends_with_the_calls_the_model_asked_for()
    {
        int toolRuns = 0;
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("lookup", """{"q":"x"}""");
        AIFunction real = AIFunctionFactory.Create((string q) => { toolRuns++; return "x"; }, "lookup", "d");
        ChatClientAgent agent = AfHarness.AgentWith(model, "a", new ApprovalRequiredAIFunction(real));
        AgentSession session = await agent.CreateSessionAsync();

        // The same shaping the step does: plain declarations, so no approval requirement or implementation is reachable through them.
        var options = new ChatClientAgentRunOptions
        {
            ChatClientFactory = inner => new DeclareOnly(inner),
        };
        AgentResponse first = await agent.RunAsync("go", session, options);

        Assert.Equal(1, model.CallCount);
        Assert.Equal(0, toolRuns);
        FunctionCallContent call = Assert.Single(first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.DoesNotContain(first.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);

        // The session goes to JSON, comes back into a fresh object and carries on with the tool's result.
        JsonElement saved = await agent.SerializeSessionAsync(session);
        AgentSession restored = await agent.DeserializeSessionAsync(saved);
        AgentResponse second = await agent.RunAsync([new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, "shipped")])], restored, options);
        Assert.Equal(("all done", 2), (second.Text, model.CallCount));
    }

    private sealed class DeclareOnly(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatOptions shaped = options?.Clone() ?? new ChatOptions();
            shaped.Tools = [.. (shaped.Tools ?? []).Select(t => t is AIFunction f ? AIFunctionFactory.CreateDeclaration(f.Name, f.Description, f.JsonSchema, f.ReturnJsonSchema) : t)];
            return base.GetResponseAsync(messages, shaped, cancellationToken);
        }
    }

    [Fact]
    public async Task A_function_wrapped_for_approval_would_ask_for_approval_itself_if_the_adapter_left_it_in_place()
    {
        // The reason the step builds plain declarations: AsDeclarationOnly() keeps the link to the original, and the framework then asks for the approval it was told the function needs.
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("lookup", """{"q":"x"}""");
        AIFunction real = AIFunctionFactory.Create((string q) => "x", "lookup", "d");
        ChatClientAgent agent = AfHarness.AgentWith(model, "a", new ApprovalRequiredAIFunction(real));
        var linked = new ChatClientAgentRunOptions { ChatClientFactory = inner => new Linked(inner) };

        AgentResponse response = await agent.RunAsync("go", await agent.CreateSessionAsync(), linked);

        Assert.Contains(response.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
    }

    private sealed class Linked(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ChatOptions shaped = options?.Clone() ?? new ChatOptions();
            shaped.Tools = [.. (shaped.Tools ?? []).Select(t => t is AIFunction f ? f.AsDeclarationOnly() : t)];
            return base.GetResponseAsync(messages, shaped, cancellationToken);
        }
    }

    [Fact]
    public async Task The_chat_client_factory_of_a_run_wraps_the_clients_the_agent_calls_and_is_applied_inside_middleware()
    {
        ScriptedChatClient model = AfHarness.Echo();
        ChatClientAgent inner = AfHarness.AgentWith(model, "a");
        AIAgent wrapped = inner.AsBuilder().Use((agent, _) => agent).Build();
        bool factoryRan = false;

        await wrapped.RunAsync("hi", await wrapped.CreateSessionAsync(), new ChatClientAgentRunOptions { ChatClientFactory = c => { factoryRan = true; return c; } });

        Assert.True(factoryRan);
    }

    // ---- what can be read from an agent ------------------------------------------------------------------------------

    [Fact]
    public void The_definition_can_be_read_through_wrappers()
    {
        AIFunction tool = AIFunctionFactory.Create((string q) => q, "lookup", "d");
        ChatClientAgent agent = new(AfHarness.Echo(), new ChatClientAgentOptions { Id = "id-1", Name = "a", ChatOptions = new ChatOptions { Instructions = "be brief", Tools = [new ApprovalRequiredAIFunction(tool)], ModelId = "m-1", MaxOutputTokens = 77 } });
        AIAgent wrapped = agent.AsBuilder().Use((inner, _) => inner).Build();

        ChatOptions options = wrapped.GetService<ChatOptions>()!;
        Assert.Equal(("be brief", "m-1", 77), (options.Instructions, options.ModelId, options.MaxOutputTokens));
        AITool read = Assert.Single(options.Tools!);
        Assert.NotNull(read.GetService<ApprovalRequiredAIFunction>());
        Assert.Same(agent, wrapped.GetService<ChatClientAgent>());
        Assert.NotNull(wrapped.GetService<ChatClientAgentOptions>());
        Assert.Equal("be brief", wrapped.GetService<ChatClientAgent>()!.Instructions);
        Assert.Null(wrapped.GetService<ToolApprovalAgent>());
        Assert.NotNull(agent.AsBuilder().Use(i => new ToolApprovalAgent(i, new ToolApprovalAgentOptions())).Build().GetService<ToolApprovalAgent>());
    }

    [Fact]
    public async Task An_agent_run_as_a_function_is_recognised_even_inside_an_approval_wrapper()
    {
        ChatClientAgent agent = AfHarness.AgentWith(AfHarness.Echo(), "researcher");
        AIFunction nested = agent.AsAIFunction();

        Assert.True(AgentDefinition.IsAgentAsFunction(nested));
        Assert.True(AgentDefinition.IsAgentAsFunction(new ApprovalRequiredAIFunction(nested)));
        Assert.False(AgentDefinition.IsAgentAsFunction(AIFunctionFactory.Create((string q) => q, "x", "d")));
        Assert.Equal("researcher", nested.Name);
        Assert.NotNull(await Task.FromResult(nested.JsonSchema.GetRawText()));
    }

    // ---- approvals ---------------------------------------------------------------------------------------------------

    [Fact]
    public void An_approval_request_survives_JSON_and_makes_its_response()
    {
        var request = new ToolApprovalRequestContent("r1", new FunctionCallContent("c1", "lookup", AfHarness.Args("""{"q":"x"}""")));

        JsonElement json = JsonSerializer.SerializeToElement<AIContent>(request, AIJsonUtilities.DefaultOptions);
        var back = Assert.IsType<ToolApprovalRequestContent>(JsonSerializer.Deserialize<AIContent>(json, AIJsonUtilities.DefaultOptions));
        ToolApprovalResponseContent response = back.CreateResponse(false, "no");

        Assert.Equal("toolApprovalRequest", json.GetProperty("$type").GetString());
        Assert.Equal(("r1", false, "no"), (response.RequestId, response.Approved, response.Reason));
        Assert.Equal("lookup", ((FunctionCallContent)response.ToolCall).Name);
    }

    // ---- the session snapshot ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_shape_of_a_session_snapshot_is_the_one_the_redactor_was_written_for()
    {
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("lookup", """{"q":"x"}""", "all done");
        AIFunction lookup = AIFunctionFactory.Create((string q) => "found", "lookup", "d");
        ChatClientAgent agent = AfHarness.AgentWith(model, "a", lookup);
        AgentSession session = await agent.CreateSessionAsync();

        await agent.RunAsync("hello", session);

        Assert.Equal(
            """{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[{"role":"user","contents":[{"$type":"text","text":"hello"}]},{"authorName":"a","role":"assistant","contents":[{"$type":"functionCall","name":"lookup","arguments":{"q":"x"},"informationalOnly":true,"callId":"call-1"}]},{"authorName":"a","role":"tool","contents":[{"$type":"functionResult","result":"found","callId":"call-1"}]},{"authorName":"a","role":"assistant","contents":[{"$type":"text","text":"all done"}]}]}}}""",
            (await agent.SerializeSessionAsync(session)).GetRawText());
    }

    [Fact]
    public async Task The_shape_of_a_session_with_a_pending_approval_is_the_one_the_redactor_was_written_for()
    {
        ScriptedChatClient model = AfHarness.CallsToolThenAnswers("issue_refund", """{"q":"x"}""");
        ChatClientAgent agent = AfHarness.AgentWith(model, "a", new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string q) => "x", "issue_refund", "d")));
        AgentSession session = await agent.CreateSessionAsync();

        await agent.RunAsync("go", session);

        Assert.Equal(
            """{"stateBag":{"_pendingApprovalRequests":[{"toolCall":{"$type":"functionCall","name":"issue_refund","arguments":{"q":"x"},"informationalOnly":false,"callId":"call-1"},"requiresConfirmation":true,"requestId":"ficc_call-1"}],"InMemoryChatHistoryProvider":{"messages":[{"role":"user","contents":[{"$type":"text","text":"go"}]},{"authorName":"a","role":"assistant","contents":[{"$type":"toolApprovalRequest","toolCall":{"$type":"functionCall","name":"issue_refund","arguments":{"q":"x"},"informationalOnly":false,"callId":"call-1"},"requiresConfirmation":true,"requestId":"ficc_call-1"}]}]}}}""",
            (await agent.SerializeSessionAsync(session)).GetRawText());
    }

    [Fact]
    public void Every_content_type_the_library_can_write_is_either_known_to_the_redactor_or_deliberately_refused()
    {
        string[] deliberatelyRefused =
        [
            "usage", "hostedFile", "hostedVectorStore", "toolCall", "toolResult", "inputRequest", "inputResponse", "mcpServerToolCall", "mcpServerToolResult", "imageGenerationToolCall",
            "imageGenerationToolResult", "codeInterpreterToolCall", "codeInterpreterToolResult", "webSearchToolCall", "webSearchToolResult",
        ];
        string[] inLibrary = [.. typeof(AIContent).GetCustomAttributes<JsonDerivedTypeAttribute>().Select(a => a.TypeDiscriminator!.ToString()!)];

        // A new content type in a new version is neither: that is the failure, and the decision (redact it, or refuse it) is the person re-pinning's.
        string[] undecided = [.. inLibrary.Where(t => !SnapshotRedactor.KnownContentTypes.Contains(t) && !deliberatelyRefused.Contains(t))];
        Assert.Empty(undecided);
        Assert.All(SnapshotRedactor.KnownContentTypes, t => Assert.Contains(t, inLibrary)); // and nothing the redactor knows has gone away
    }
}
