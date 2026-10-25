using System.Text;
using Kairos.Agents;
using Kairos.Testing;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

/// <summary>The real pieces in one process (in-memory store, worker runtime, agent runtime, virtual clock) for agent workflow tests.</summary>
internal sealed class AgentHarness
{
    private static readonly TenantScope DefaultScope = new(Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000b1"));

    private readonly WorkflowRegistry _workflows = new();
    private readonly ActivityRegistry _activities = new();
    private readonly List<RunKey> _runs = [];
    /// <summary>The key the "host" signs approvals with and the workers verify with.</summary>
    public static readonly HmacApprovalTokens ApprovalTokens = new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    private readonly IPayloadProtector? _protector;
    private int _counter;

    public AgentHarness(Action<AgentsBuilder> configure, bool faultInjection = false, VirtualTimeProvider? time = null, ulong seed = 1, IPayloadProtector? protector = null, bool verifyApprovers = true)
    {
        Time = time ?? new VirtualTimeProvider();
        var memory = new InMemoryKairosStore();
        MemoryStore = memory;
        if (faultInjection)
        {
            Faults = new FaultInjectingStore(memory, new SimRandom(seed).Fork(1), new SimFaultOptions(), Stats);
        }

        Store = (IKairosStore?)Faults ?? memory;
        _protector = protector;
        Client = new KairosClient(Store, Time, payloadProtector: protector);
        Agent = new AgentClient(Client, Time, ApprovalTokens);

        var builder = new AgentsBuilder().UseTimeProvider(Time).UseTimeProvider(Time);
        if (verifyApprovers)
        {
            builder.UseApproverVerifier(ApprovalTokens);
        }
        configure(builder);
        Runtime = builder.Build();
        Runtime.RegisterInto(_activities);
        Worker = NewWorker();
    }

    public VirtualTimeProvider Time { get; }

    public InMemoryKairosStore MemoryStore { get; }

    public IKairosStore Store { get; }

    public SimStats Stats { get; } = new();

    /// <summary>The fault layer in front of the store, when the harness was built with fault injection.</summary>
    public FaultInjectingStore? Faults { get; }

    public KairosClient Client { get; }

    public AgentClient Agent { get; }

    public AgentsRuntime Runtime { get; }

    public WorkerRuntime Worker { get; private set; }

    public FakeExternalSystem External { get; } = new();

    public static TenantScope Scope => DefaultScope;

    public WorkerRuntime NewWorker() => new(Store, DefaultScope, _workflows, _activities, Time, new WorkerOptions { AutoHeartbeat = false, FailedTaskRetryDelay = TimeSpan.FromSeconds(1) }, _protector);

    public void Workflow(string type, Func<IAgentContext, byte[], Task<byte[]>> body) =>
        _workflows.Register(type, KairosClient.DefaultBuildId, Runtime.Workflow(body));

    public void Activity(string type, ActivityFunc activity) => _activities.Register(type, activity);

    public async Task<RunKey> StartAsync(string type, string input = "", string? id = null)
    {
        string workflowId = id ?? $"wf-{_counter++}";
        StartRunResult result = await Retry(async () => await Client.StartAsync(DefaultScope, workflowId, type, Encoding.UTF8.GetBytes(input)));
        var key = new RunKey(DefaultScope.TenantId, DefaultScope.NamespaceId, result.RunId);
        _runs.Add(key);
        return key;
    }

    /// <summary>Drives until the run closes or only an outside event (a signal) can move it. The clock jumps to the next wakeup when idle.</summary>
    public async Task<RunState> RunAsync(RunKey run, bool jumpClock = true)
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

            RunState state = (await Client.GetStateAsync(run))!.Value;
            if (state != RunState.Running || !jumpClock || MemoryStore.NextWakeup(DefaultScope, Time.GetUtcNow()) is not { } wake)
            {
                return state;
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

    public async Task<IReadOnlyList<HistoryEvent>> HistoryAsync(RunKey run) => await Client.GetOpenedHistoryAsync(run);

    public async Task<string> ResultTextAsync(RunKey run) =>
        Encoding.UTF8.GetString(((WorkflowCompleted)(await HistoryAsync(run))[^1]).Result);

    public async Task<WorkflowFailed> FailureAsync(RunKey run) => (WorkflowFailed)(await HistoryAsync(run))[^1];

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>A model that answers a fixed text.</summary>
    public static ScriptedChatClient Echo(string answer = "done") => new(_ => new ChatMessage(ChatRole.Assistant, answer));

    /// <summary>The first time it is asked it calls <paramref name="tool"/>; once a tool result is in the conversation it answers.</summary>
    public static ScriptedChatClient CallsToolThenAnswers(string tool, string argumentsJson = "{}", string answer = "all done") => new(messages =>
    {
        bool hasResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
        if (hasResult)
        {
            return new ChatMessage(ChatRole.Assistant, answer);
        }

        return new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", tool, WireArgs(argumentsJson))]);
    });

    public static IDictionary<string, object?> WireArgs(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
    }

    /// <summary>The usual agent loop: ask, run the tools the model asks for, repeat until it answers.</summary>
    public static async Task<string> AgentLoopAsync(IAgentContext ctx, string model, string userText, params string[] tools)
    {
        var conversation = new List<ChatMessage> { new(ChatRole.User, userText) };
        AgentToolSet set = ctx.Tools(tools);
        for (int turn = 0; turn < 8; turn++)
        {
            AgentReply reply = await ctx.ChatAsync(model, conversation, set);
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

        return "gave up";
    }

    public static AgentTool ReadTool(string name, Func<ToolInvocation, string> handler) =>
        new(name, name, """{"type":"object","properties":{}}""", ToolAccess.ReadOnly, (i, _) => ValueTask.FromResult(handler(i)));

    public static AgentTool WriteTool(string name, Func<ToolInvocation, string> handler) =>
        new(name, name, """{"type":"object","properties":{}}""", ToolAccess.SideEffecting, (i, _) => ValueTask.FromResult(handler(i)));
}
