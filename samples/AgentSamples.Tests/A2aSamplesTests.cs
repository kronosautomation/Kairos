using System.Security.Cryptography;
using System.Text;
using A2A;
using Kairos.Agents;
using Kairos.Agents.A2aClient;
using Kairos.Testing;

namespace Kairos.Samples.Agents.Tests;

public sealed class A2aSamplesTests
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static AgentCard Card() => new()
    {
        Name = "Billing",
        Description = "Answers billing questions.",
        Version = "1",
        SupportedInterfaces = [new AgentInterface { Url = "https://agents.example.com/billing/a2a", ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
        Capabilities = new AgentCapabilities(),
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "status", Name = "Status", Description = "Invoice status.", Tags = ["billing"] }],
    };

    [Fact]
    public void A_signed_card_is_trusted_and_a_changed_or_foreign_one_is_not()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        (AgentCard signed, byte[] publicKey) = A2aExamples.SignCard(Card(), key);

        Assert.True(A2aExamples.IsTrusted(signed, publicKey));
        Assert.False(A2aExamples.IsTrusted(Card(), publicKey));
        signed.Name = "Billing (altered)";
        Assert.False(A2aExamples.IsTrusted(signed, publicKey));
        (AgentCard foreign, _) = A2aExamples.SignCard(Card(), other);
        Assert.False(A2aExamples.IsTrusted(foreign, publicKey));
    }

    private sealed class Billing : IA2aAgentEndpoint
    {
        public bool Asks { get; init; }

        public List<(string? Text, string? TaskId)> Received { get; } = [];

        public ValueTask<A2aReply> SendAsync(string messageId, string? text, string? dataJson, string? taskId, string? contextId, CancellationToken cancellationToken)
        {
            Received.Add((text, taskId));
            return ValueTask.FromResult(Asks && taskId is null
                ? new A2aReply(A2aState.InputRequired, null, null, "t-1", "c-1", "Which customer?")
                : new A2aReply(A2aState.Message, "Invoice 42 is paid.", null, taskId, "c-1", null));
        }

        public ValueTask<A2aReply> GetTaskAsync(string taskId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask CancelTaskAsync(string taskId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static async Task<(string Result, Billing Agent)> RunAsync(bool asks)
    {
        var time = new VirtualTimeProvider();
        var store = new InMemoryKairosStore();
        var client = new KairosClient(store, time);
        var agents = new AgentsBuilder().UseTimeProvider(time).AllowUnverifiedApprovers().Build();
        var billing = new Billing { Asks = asks };
        var activities = new ActivityRegistry();
        var workflows = new WorkflowRegistry();
        agents.RegisterInto(activities);
        new A2aClientsBuilder().AddAgent("billing", billing).ReadOnlyAgents("billing").Build().RegisterInto(activities);
        workflows.Register("collect", KairosClient.DefaultBuildId, agents.Workflow(A2aExamples.CollectInvoiceAsync));
        var worker = new WorkerRuntime(store, Scope, workflows, activities, time, new WorkerOptions { AutoHeartbeat = false });
        StartRunResult start = await client.StartAsync(Scope, "inv-42", "collect", Encoding.UTF8.GetBytes("42"));
        await Driver.DriveAsync(worker);
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        return (Encoding.UTF8.GetString(((WorkflowCompleted)(await client.GetOpenedHistoryAsync(run))[^1]).Result), billing);
    }

    [Fact]
    public async Task The_workflow_asks_the_agent_and_records_the_answer()
    {
        (string result, Billing agent) = await RunAsync(asks: false);

        Assert.Equal("Invoice 42 is paid.", result);
        Assert.Single(agent.Received);
    }

    [Fact]
    public async Task A_question_from_the_agent_is_answered_on_the_same_task()
    {
        (string result, Billing agent) = await RunAsync(asks: true);

        Assert.Equal("Invoice 42 is paid.", result);
        Assert.Equal(("The invoice is for customer 42.", "t-1"), agent.Received[^1]);
    }

    [Fact]
    public async Task An_unsigned_partner_is_registered_as_an_explicit_exception()
    {
        using var http = new HttpClient();
        A2aClientRuntime runtime = A2aExamples.ConfigureUnsignedPartner(http);

        Assert.Contains(A2aClientRuntime.SendActivity, runtime.Activities.Keys);
        using var strict = new A2aAgentConnector(new Uri("https://partner.example.com/"), http);
        Assert.True(strict.RequireSignedCard);
        await Assert.ThrowsAsync<UntrustedAgentCardException>(async () => await strict.ConnectAsync(default));
    }
}
