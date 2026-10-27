using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using AfContext = Microsoft.Agents.AI.Workflows.IWorkflowContext;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// What a long workflow with a chat history in its messages costs in history, and what it costs to ask where a waiting workflow stands. The tests print their figures (<c>[measure]</c> lines) and assert only the shape;
/// the review note quotes the figures.
/// </summary>
public sealed class WorkflowMeasurementTests
{
    private const int Supersteps = 100;

    /// <summary>Adds a reply of about 3 KB to the conversation it is handed and passes it on: the history that travels from executor to executor.</summary>
    private sealed class Grow(string id) : Executor<List<ChatMessage>, List<ChatMessage>>(id)
    {
        public override ValueTask<List<ChatMessage>> HandleAsync(List<ChatMessage> message, AfContext context, CancellationToken cancellationToken)
        {
            message.Add(new ChatMessage(ChatRole.Assistant, MeasurementTests.Record(3, message.Count)));
            return new(message);
        }
    }

    /// <summary>The same, with the history kept in a scope every executor shares (executor state under one name) and only a token passed on.</summary>
    private sealed class GrowInState(string id) : Executor<string, string>(id)
    {
        public override async ValueTask<string> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
        {
            List<ChatMessage> history = await context.ReadOrInitStateAsync("history", () => new List<ChatMessage>(), "chat", cancellationToken);
            history.Add(new ChatMessage(ChatRole.Assistant, MeasurementTests.Record(3, history.Count)));
            await context.QueueStateUpdateAsync("history", history, "chat", cancellationToken);
            return message;
        }
    }

    private sealed class AskAboutState() : Executor<string, Ticket>("ask-person")
    {
        public override async ValueTask<Ticket> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
        {
            List<ChatMessage> history = await context.ReadStateAsync<List<ChatMessage>>("history", "chat", cancellationToken) ?? [];
            return new Ticket("t", $"{history.Count} messages");
        }
    }

    private sealed class Start() : Executor<string, List<ChatMessage>>("start")
    {
        public override ValueTask<List<ChatMessage>> HandleAsync(string message, AfContext context, CancellationToken cancellationToken) => new([new ChatMessage(ChatRole.User, message)]);
    }

    private sealed class AskPerson() : Executor<List<ChatMessage>, Ticket>("ask-person")
    {
        public override ValueTask<Ticket> HandleAsync(List<ChatMessage> message, AfContext context, CancellationToken cancellationToken) => new(new Ticket("t", $"{message.Count} messages"));
    }

    private static Workflow BuildState()
    {
        ExecutorBinding start = new PassOn().BindExecutor();
        var builder = new WorkflowBuilder(start);
        ExecutorBinding previous = start;
        for (int i = 0; i < Supersteps - 3; i++)
        {
            ExecutorBinding next = new GrowInState("g" + i).BindExecutor();
            builder.AddEdge(previous, next);
            previous = next;
        }

        ExecutorBinding ask = new AskAboutState().BindExecutor();
        ExecutorBinding gate = RequestPort.Create<Ticket, string>("reply").BindAsExecutor();
        ExecutorBinding done = new ConcludeText("done", new Ledger()).BindExecutor();
        return builder.AddEdge(previous, ask).AddEdge(ask, gate).AddEdge(gate, done).WithOutputFrom(done).Build();
    }

    private sealed class PassOn() : Executor<string, string>("start")
    {
        public override ValueTask<string> HandleAsync(string message, AfContext context, CancellationToken cancellationToken) => new(message);
    }

    private static Workflow Build()
    {
        ExecutorBinding start = new Start().BindExecutor();
        var builder = new WorkflowBuilder(start);
        ExecutorBinding previous = start;
        for (int i = 0; i < Supersteps - 3; i++)
        {
            ExecutorBinding next = new Grow("g" + i).BindExecutor();
            builder.AddEdge(previous, next);
            previous = next;
        }

        ExecutorBinding ask = new AskPerson().BindExecutor();
        ExecutorBinding gate = RequestPort.Create<Ticket, string>("reply").BindAsExecutor();
        ExecutorBinding done = new ConcludeText("done", new Ledger()).BindExecutor();
        return builder.AddEdge(previous, ask).AddEdge(ask, gate).AddEdge(gate, done).WithOutputFrom(done).Build();
    }

    private static long Size(HistoryEvent e) => e switch
    {
        ActivityScheduled s => s.Input.Length,
        ActivityCompleted c => c.Result.Length,
        SignalReceived s => s.Payload.Length,
        WorkflowStarted w => w.Input.Length,
        WorkflowContinued c => c.Input.Length,
        WorkflowCompleted c => c.Result.Length,
        _ => 0,
    };

    private static AfHarness Harness(int fullEvery, int supersteps, List<CountingStore> counting, bool inState = false) =>
        new(af => af
            .Configure(o =>
            {
                o.FullSnapshotEvery = fullEvery;
                o.SuperstepsPerStep = supersteps;
                o.MaxCheckpointBytes = 16 * 1024 * 1024;
            })
            .AddWorkflow<string>("long", _ => inState ? BuildState() : Build(), r => r.JsonOptions(AIJsonUtilities.DefaultOptions)),
            wrapStore: store =>
            {
                var c = new CountingStore(store);
                counting.Add(c);
                return c;
            });

    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(16, 1, false)]
    [InlineData(16, 5, false)]
    [InlineData(1, 1, true)]
    [InlineData(16, 1, true)]
    [InlineData(16, 5, true)]
    public async Task What_a_workflow_of_a_hundred_supersteps_with_a_chat_history_costs_in_history(int fullEvery, int supersteps, bool inState)
    {
        var counting = new List<CountingStore>();
        AfHarness h = Harness(fullEvery, supersteps, counting, inState);
        DurableWorkflowRun run = await AfHarness.Retry(async () => await h.Workflows.Get("long").StartAsync("write me a long story", "w1").AsTask());

        await h.DriveAsync();
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run.Run);

        AfwStepResult[] results = [.. history.OfType<ActivityCompleted>().Select(c => AfCodec.Read(c.Result, AfwJson.Default.AfwStepResult))];
        long total = history.Sum(Size);
        long checkpointBytes = results.Max(r => r.CheckpointBytes ?? 0);
        int whole = results.Count(r => r.Checkpoint is not null);
        Console.Error.WriteLine(
            $"[measure] history in {(inState ? "shared state" : "the messages passed on")}, FullSnapshotEvery={fullEvery}, SuperstepsPerStep={supersteps}: {results.Length} steps, {results.Sum(r => r.Supersteps)} supersteps; history {total:N0} bytes ({total / Math.Max(1, results.Length):N0} per step); " +
            $"largest checkpoint {checkpointBytes:N0} bytes; {whole} steps record it whole, {results.Length - whole} as changes");
        Assert.True(results.Sum(r => r.Supersteps) >= Supersteps - 2);
        Assert.True(checkpointBytes > 100 * 1024, "the checkpoint should have grown with the history it carries");
    }

    [Fact]
    public async Task What_it_costs_to_ask_where_a_waiting_workflow_stands()
    {
        var counting = new List<CountingStore>();
        AfHarness h = Harness(16, 1, counting);
        DurableWorkflowRun run = await AfHarness.Retry(async () => await h.Workflows.Get("long").StartAsync("write me a long story", "w1").AsTask());
        await h.DriveAsync();
        CountingStore store = counting[0];

        store.Reset();
        WorkflowView first = await run.ReadAsync();
        (long firstCalls, long firstEvents, long firstBytes) = (store.Calls, store.Events, store.Bytes);
        store.Reset();
        WorkflowView again = await run.ReadAsync();
        (long againCalls, long againEvents, long againBytes) = (store.Calls, store.Events, store.Bytes);
        store.Reset();
        IReadOnlyList<HistoryEvent> whole = await h.Client.GetOpenedHistoryAsync(run.Run);
        long wholeBytes = store.Bytes;

        Console.Error.WriteLine(
            $"[measure] a waiting workflow ({first.Steps} steps): first look {firstCalls} call(s), {firstEvents:N0} events, {firstBytes:N0} bytes; a look when nothing was written {againCalls} call(s), {againEvents} events, {againBytes} bytes; " +
            $"reading the whole history instead: {whole.Count:N0} events, {wholeBytes:N0} bytes");
        Assert.Equal(DurableWorkflowStatus.Waiting, first.Status);
        Assert.Equal(first.Steps, again.Steps);
        Assert.Equal(0, againEvents);
    }
}
