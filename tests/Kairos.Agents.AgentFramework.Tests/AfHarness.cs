using System.Text;
using Kairos.Agents;
using Kairos.Agents.AgentFramework;
using Kairos.Testing;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>The real pieces in one process (in-memory store, worker runtime, the adapter, virtual clock) for tests of durable Agent Framework agents.</summary>
internal sealed class AfHarness
{
    private static readonly TenantScope DefaultScope = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000b1"));

    /// <summary>The key the "host" signs approvals with. Workers get only its public half.</summary>
    public static readonly LocalApprovalEvidenceSigner ApprovalSigner = LocalApprovalEvidenceSigner.Create("approvals-test-1");

    public static EsApproverVerifier NewVerifier() => new(new Dictionary<string, byte[]> { [ApprovalSigner.KeyId] = ApprovalSigner.PublicKey });

    private readonly WorkflowRegistry _workflows = new();
    private readonly ActivityRegistry _activities = new();
    private readonly IPayloadProtector? _protector;

    public AfHarness(Action<AgentFrameworkBuilder> configure, bool faultInjection = false, ulong seed = 1, IPayloadProtector? protector = null, bool verifyApprovers = true)
    {
        Time = new VirtualTimeProvider();
        var memory = new InMemoryKairosStore();
        MemoryStore = memory;
        if (faultInjection)
        {
            Faults = new FaultInjectingStore(memory, new SimRandom(seed).Fork(1), new SimFaultOptions(), Stats);
        }

        Store = (IKairosStore?)Faults ?? memory;
        _protector = protector;
        Client = new KairosClient(Store, Time, payloadProtector: protector);
        Approvals = new AgentClient(Client, Time, ApprovalSigner);

        var builder = new AgentFrameworkBuilder();
        builder.ConfigureAgents(b =>
        {
            b.UseTimeProvider(Time);
            if (verifyApprovers)
            {
                b.UseApproverVerifier(NewVerifier());
            }
        });
        configure(builder);
        Runtime = AgentFrameworkRuntime.Create(builder);
        Runtime.Bind(Client);
        Runtime.RegisterInto(_activities, _workflows);
        Agents = new DurableAgents(Runtime, Client, DefaultScope, Time);
        Worker = NewWorker();
    }

    public VirtualTimeProvider Time { get; }

    public InMemoryKairosStore MemoryStore { get; }

    public IKairosStore Store { get; }

    public SimStats Stats { get; } = new();

    public FaultInjectingStore? Faults { get; }

    public KairosClient Client { get; }

    public AgentClient Approvals { get; }

    public AgentFrameworkRuntime Runtime { get; }

    public DurableAgents Agents { get; }

    public WorkerRuntime Worker { get; private set; }

    public static TenantScope Scope => DefaultScope;

    public WorkerRuntime NewWorker() => new(Store, DefaultScope, _workflows, _activities, Time, new WorkerOptions { AutoHeartbeat = false, FailedTaskRetryDelay = TimeSpan.FromSeconds(1) }, _protector);

    public void Activity(string type, ActivityFunc activity) => _activities.Register(type, activity);

    /// <summary>Drives the worker until nothing more can happen without an outside event (a message, an approval). The clock jumps to the next wakeup when idle. A simulated crash replaces the worker.</summary>
    public async Task DriveAsync(bool jumpClock = true)
    {
        while (true)
        {
            try
            {
                await Driver.DriveAsync(Worker);
            }
            catch (SimulatedFaultException)
            {
                Worker = NewWorker(); // the process died; a new one takes over
                continue;
            }

            if (!jumpClock || MemoryStore.NextWakeup(DefaultScope, Time.GetUtcNow()) is not { } wake)
            {
                return;
            }

            Time.AdvanceTo(wake);
        }
    }

    /// <summary>Repeats a client call whose outcome a simulated crash hid, as a real caller would.</summary>
    public static async Task<T> Retry<T>(Func<Task<T>> call)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (SimulatedFaultException) when (attempt < 50)
            {
            }
        }
    }

    /// <summary>Sends a message to a durable agent's conversation and drives the worker until nothing more can happen; returns the answer when there is one.</summary>
    public async Task<(AgentResponse? Reply, DurableTurn Turn)> AskAsync(string agent, string session, string text, string? onBehalfOf = null)
    {
        DurableAIAgent durable = Agents.Get(agent);
        DurableAgentSession s = durable.Session(session);
        s.OnBehalfOf ??= onBehalfOf;
        DurableTurn turn = await Retry(async () => await durable.SendAsync([new ChatMessage(ChatRole.User, text)], s).AsTask());
        await DriveAsync();
        AgentResponse? reply = await ReadAsync(agent, turn);
        return (reply, turn);
    }

    public async Task<AgentResponse?> ReadAsync(string agent, DurableTurn turn) => await Retry(async () => await Agents.Get(agent).TryReadAsync(turn).AsTask());

    public async Task<IReadOnlyList<HistoryEvent>> HistoryAsync(RunKey run) => await Retry(async () => await Client.GetOpenedHistoryAsync(run).AsTask());

    /// <summary>The run that holds a conversation now.</summary>
    public async Task<RunKey> RunOfAsync(string agent, string session)
    {
        StartRunResult current = await Retry(async () => await Client.StartAsync(DefaultScope, $"af/{agent}/{session}", AgentFrameworkNames.Conversation, Bytes("{\"agent\":\"" + agent + "\"}")).AsTask());
        return new RunKey(DefaultScope.TenantId, DefaultScope.NamespaceId, current.RunId);
    }

    public static string Text(AgentResponse? response) => response?.Text ?? "<no answer>";

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The usual support agent: looks an order up (read-only), refunds it (the author wrapped the tool in an approval requirement).</summary>
    public static ChatClientAgent SupportAgent(IChatClient model, Func<string, string>? lookup = null, Func<string, string>? refund = null, string name = "support")
    {
        AIFunction lookupTool = AIFunctionFactory.Create((string orderId) => lookup?.Invoke(orderId) ?? "shipped", "lookup_order", "Finds an order and says where it is.");
        AIFunction refundTool = AIFunctionFactory.Create((string orderId) => refund?.Invoke(orderId) ?? "refunded", "issue_refund", "Refunds an order.");
        return new ChatClientAgent(model, new ChatClientAgentOptions
        {
            Name = name,
            Description = "Customer support",
            ChatOptions = new ChatOptions { Instructions = "You help customers with their orders.", Tools = [lookupTool, new ApprovalRequiredAIFunction(refundTool)] },
        });
    }

    /// <summary>A model that looks the order up, then refunds it, then answers.</summary>
    public static ScriptedChatClient LookupRefundAnswer(string answer = "Refund issued.", Action<IReadOnlyList<ChatMessage>>? observe = null) => new(messages =>
    {
        observe?.Invoke(messages);
        int results = messages.Sum(m => m.Contents.OfType<FunctionResultContent>().Count());
        return results switch
        {
            0 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup_order", Args("""{"orderId":"A-1042"}"""))]),
            1 => new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-2", "issue_refund", Args("""{"orderId":"A-1042"}"""))]),
            _ => new ChatMessage(ChatRole.Assistant, answer),
        };
    });

    public static IDictionary<string, object?> Args(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }
}
