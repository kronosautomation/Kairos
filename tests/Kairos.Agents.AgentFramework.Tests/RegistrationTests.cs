using Kairos.Agents;
using Kairos.Testing.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>What registration refuses, and the message that names the way out.</summary>
public sealed class RegistrationTests
{
    private static AgentFrameworkRuntime Create(Action<AgentFrameworkBuilder> configure)
    {
        var builder = new AgentFrameworkBuilder();
        configure(builder);
        return AgentFrameworkRuntime.Create(builder);
    }

    private static ChatClientAgent AgentWith(string? name, params AITool[] tools) =>
        new(AfHarness.Echo(), new ChatClientAgentOptions { Name = name, ChatOptions = new ChatOptions { Instructions = "x", Tools = [.. tools] } });

    private static AIFunction Tool(string name = "lookup") => AIFunctionFactory.Create((string q) => q, name, "d");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    public void An_agent_needs_a_stable_name(string? name)
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith(name))));
        Assert.Contains("stable name", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_agents_cannot_share_a_name()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a")).AddAgent(AgentWith("a"))));
        Assert.Contains("Two agents are registered as 'a'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_that_cannot_be_stepped_is_refused_with_the_option_that_allows_it()
    {
        var opaque = new OpaqueAgent("remote", (_, _) => "hi");
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(opaque)));
        Assert.Contains("AllowTurnLevel()", e.Message, StringComparison.Ordinal);
        Assert.Contains("not a ChatClientAgent", e.Message, StringComparison.Ordinal);

        AgentFrameworkRuntime runtime = Create(af => af.AddAgent(opaque, r => r.AllowTurnLevel()));
        Assert.False(runtime.Definitions["remote"].Stepped);
    }

    [Fact]
    public void A_tool_approval_wrapper_and_a_loop_wrapper_make_an_agent_turn_level_only()
    {
        AIAgent approving = AgentWith("a", Tool()).AsBuilder().Use(inner => new ToolApprovalAgent(inner, new ToolApprovalAgentOptions())).Build();
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(approving)));
        Assert.Contains("ToolApprovalAgent", e.Message, StringComparison.Ordinal);
        Assert.Contains("AllowTurnLevel()", e.Message, StringComparison.Ordinal);
        Assert.False(Create(af => af.AddAgent(approving, r => r.AllowTurnLevel())).Definitions["a"].Stepped);
    }

    [Fact]
    public void A_transparent_wrapper_is_kept_and_the_agent_is_still_stepped()
    {
        AIAgent wrapped = AgentWith("a", Tool()).AsBuilder().Use((inner, _) => inner).Build();
        Assert.True(Create(af => af.AddAgent(wrapped, r => r.ReadOnly("lookup"))).Definitions["a"].Stepped);
    }

    [Fact]
    public void An_agent_run_as_a_tool_is_refused_unless_nesting_is_allowed_by_name()
    {
        ChatClientAgent inner = AgentWith("researcher");
        AIFunction nested = inner.AsAIFunction();
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("manager", nested))));
        Assert.Contains("AllowOpaqueNesting(\"researcher\")", e.Message, StringComparison.Ordinal);
        Assert.Contains("AsAIFunction()", e.Message, StringComparison.Ordinal);

        AgentFrameworkRuntime runtime = Create(af => af.AddAgent(AgentWith("manager", nested), r => r.AllowOpaqueNesting("researcher")));
        Assert.Single(runtime.Definitions["manager"].Tools);
    }

    [Fact]
    public void An_agent_wrapped_in_an_approval_requirement_is_still_recognised_as_a_nested_agent()
    {
        AIFunction nested = new ApprovalRequiredAIFunction(AgentWith("researcher").AsAIFunction());
        Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("manager", nested))));
    }

    [Fact]
    public void A_durable_agent_used_as_a_tool_must_itself_be_registered()
    {
        var other = new DurableAgentFunction("ghost", "boo");
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("manager", other))));
        Assert.Contains("'ghost', which is not registered", e.Message, StringComparison.Ordinal);
        Create(af => af.AddAgent(AgentWith("manager", other)).AddAgent(AgentWith("ghost")));
    }

    [Fact]
    public void An_agent_with_a_hosted_tool_runs_a_turn_at_a_time_or_not_at_all()
    {
        var hosted = new HostedWebSearchTool();
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a", hosted))));
        Assert.Contains("hosted tool", e.Message, StringComparison.Ordinal);
        Assert.Contains("outside the Kairos tool policy", e.Message, StringComparison.Ordinal);
        Assert.Contains("AllowTurnLevel()", e.Message, StringComparison.Ordinal);

        Assert.False(Create(af => af.AddAgent(AgentWith("a", hosted), r => r.AllowTurnLevel())).Definitions["a"].Stepped);
    }

    [Fact]
    public void A_tool_that_is_only_a_declaration_is_refused()
    {
        AITool declaration = AIFunctionFactory.CreateDeclaration("lookup", "d", System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement);
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a", declaration))));
        Assert.Contains("only a declaration", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Marks_for_a_tool_the_agent_does_not_have_are_refused()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a", Tool()), r => r.ReadOnly("lookpu"))));
        Assert.Contains("'lookpu'", e.Message, StringComparison.Ordinal);
        Assert.Contains("lookup", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_cannot_be_marked_both_ways()
    {
        Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a", Tool()), r => r.ReadOnly("lookup").AllowWithoutApproval("lookup"))));
        Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a", Tool()), r => r.AllowWithoutApproval("lookup").RequireApproval("lookup"))));
        Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a", new ApprovalRequiredAIFunction(Tool())), r => r.AllowWithoutApproval("lookup"))));
    }

    [Fact]
    public void Two_agents_may_share_one_tool_but_not_two_different_tools_with_one_name()
    {
        AIFunction shared = Tool();
        Create(af => af.AddAgent(AgentWith("a", shared)).AddAgent(AgentWith("b", shared)));

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a", Tool())).AddAgent(AgentWith("b", Tool()))));
        Assert.Contains("'lookup'", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_budget_needs_a_model_to_reserve_by()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af
            .ConfigureAgents(b => b.UseModelCallGuard(new CountingGuard()))
            .AddAgent(AgentWith("a"))));
        Assert.Contains("Model(\"alias\"", e.Message, StringComparison.Ordinal);

        Create(af => af.ConfigureAgents(b => b.UseModelCallGuard(new CountingGuard())).AddAgent(AgentWith("a"), r => r.Model("gpt-test", 500)));
    }

    [Fact]
    public void A_redactor_refuses_a_session_that_keeps_state_it_does_not_know_unless_it_is_declared_free_of_text()
    {
        var provider = new TestStateProvider();
        ChatClientAgent agent = new(AfHarness.Echo(), new ChatClientAgentOptions { Name = "a", AIContextProviders = [provider] });
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Create(af => af.ConfigureAgents(b => b.UseRedactor(x => x)).AddAgent(agent)));
        Assert.Contains("AllowSnapshotState(\"TestState\")", e.Message, StringComparison.Ordinal);

        Create(af => af.ConfigureAgents(b => b.UseRedactor(x => x)).AddAgent(agent, r => r.AllowSnapshotState("TestState")));
        Create(af => af.AddAgent(agent)); // without a redactor nothing is promised, so nothing is refused
    }

    [Fact]
    public void Tools_nobody_classified_are_listed_by_name()
    {
        AgentFrameworkRuntime runtime = Create(af => af.AddAgent(
            AgentWith("a", Tool("read_it"), Tool("write_it"), Tool("allowed_it"), new ApprovalRequiredAIFunction(Tool("author_it")), Tool("required_it")),
            r => r.ReadOnly("read_it").AllowWithoutApproval("allowed_it").RequireApproval("required_it")));

        Assert.Equal(["write_it"], runtime.Unclassified); // the author's own approval requirement is a classification too
    }

    [Fact]
    public void Tool_names_may_not_contain_the_credential_separator()
    {
        Assert.Throws<InvalidOperationException>(() => Create(af => af.AddAgent(AgentWith("a", Tool("a:b")))));
    }

    private sealed class TestStateProvider : AIContextProvider
    {
        public override IReadOnlyList<string> StateKeys => ["TestState"];

        protected override ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = default) => new(new AIContext());
    }
}

/// <summary>A guard that reserves nothing and counts what it is asked.</summary>
internal sealed class CountingGuard : IModelCallGuard
{
    public int Reserved;
    public int Settled;
    public int Released;
    public readonly List<ModelCallInfo> Calls = [];
    public readonly List<UsageRecord?> Usage = [];

    public Func<ModelCallInfo, ModelCallPermit>? Decide { get; set; }

    public ValueTask<ModelCallPermit> ReserveAsync(ModelCallInfo request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Reserved);
        lock (Calls)
        {
            Calls.Add(request);
        }

        return ValueTask.FromResult(Decide?.Invoke(request) ?? ModelCallPermit.Allow("r-" + request.IdempotencyKey));
    }

    public ValueTask SettleAsync(ModelCallInfo request, ModelCallPermit permit, UsageRecord? usage, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Settled);
        lock (Usage)
        {
            Usage.Add(usage);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(ModelCallInfo request, ModelCallPermit permit, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Released);
        return ValueTask.CompletedTask;
    }
}
