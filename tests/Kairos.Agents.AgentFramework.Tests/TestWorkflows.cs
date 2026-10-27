using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using AfContext = Microsoft.Agents.AI.Workflows.IWorkflowContext;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>A ticket moving through a test workflow.</summary>
public sealed record Ticket(string Id, string Body);

/// <summary>What the executors of the test workflows did in the (simulated) world: how many times each ran, in total and per input.</summary>
public sealed class Ledger
{
    private readonly ConcurrentDictionary<string, int> _runs = new();

    public int Runs(string executor) => _runs.GetValueOrDefault(executor);

    public int Total => _runs.Values.Sum();

    /// <summary>Called whenever an executor runs (a test's way to do something in the middle of a superstep, such as cancelling the workflow).</summary>
    public Action<string>? OnRun { get; set; }

    public void Ran(string executor)
    {
        _runs.AddOrUpdate(executor, 1, (_, n) => n + 1);
        OnRun?.Invoke(executor);
    }

    public IReadOnlyDictionary<string, int> Snapshot => new Dictionary<string, int>(_runs);

    private readonly object _gate = new();

    /// <summary>An executor that is marked to fail raises an exception this many more times before it works.</summary>
    public int Failures { get; set; }

    public bool TryFail()
    {
        lock (_gate)
        {
            if (Failures <= 0)
            {
                return false;
            }

            Failures--;
            return true;
        }
    }
}

internal sealed class Step(string id, Ledger ledger, string suffix) : Executor<string, string>(id)
{
    public override ValueTask<string> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        if (suffix == "!boom" && ledger.TryFail())
        {
            throw new InvalidOperationException("secret detail that must not be recorded");
        }

        return new($"{message}>{Id}");
    }
}

/// <summary>Yields the text it receives as the workflow's output.</summary>
[YieldsOutput(typeof(string))]
internal sealed class Finish(string id, Ledger ledger) : Executor<string>(id)
{
    public override async ValueTask HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        await context.YieldOutputAsync(message, cancellationToken);
    }
}

internal sealed class Split(string id, Ledger ledger) : Executor<string, string>(id)
{
    public override ValueTask<string> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        return new(message);
    }
}

internal sealed class Prepare(string id, Ledger ledger) : Executor<string, Ticket>(id)
{
    public override ValueTask<Ticket> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        return new(new Ticket("t-1", message));
    }
}

[YieldsOutput(typeof(string))]
internal sealed class Conclude(string id, Ledger ledger) : Executor<bool>(id)
{
    public override async ValueTask HandleAsync(bool message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        await context.YieldOutputAsync(message ? "approved" : "rejected", cancellationToken);
    }
}

[YieldsOutput(typeof(string))]
internal sealed class ConcludeText(string id, Ledger ledger) : Executor<string>(id)
{
    public override async ValueTask HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        await context.YieldOutputAsync("answered: " + message, cancellationToken);
    }
}

/// <summary>Asks a durable agent and passes on its answer.</summary>
internal sealed class Ask(string id, Ledger ledger, DurableAgents agents, string agent) : Executor<string, string>(id)
{
    public override async ValueTask<string> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        DurableAIAgent durable = agents.Get(agent);
        AgentSession session = await durable.CreateSessionAsync(cancellationToken);
        AgentResponse reply = await durable.RunAsync(message, session, cancellationToken: cancellationToken);
        return reply.Text;
    }
}

/// <summary>Asks a durable agent and passes on its answer as a ticket for a person.</summary>
internal sealed class AskThenTicket(string id, Ledger ledger, DurableAgents agents, string agent) : Executor<string, Ticket>(id)
{
    public override async ValueTask<Ticket> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        DurableAIAgent durable = agents.Get(agent);
        AgentSession session = await durable.CreateSessionAsync(cancellationToken);
        AgentResponse reply = await durable.RunAsync(message, session, cancellationToken: cancellationToken);
        return new Ticket("t-1", reply.Text);
    }
}

internal sealed class ToTicket(string id, Ledger ledger) : Executor<string, Ticket>(id)
{
    public override ValueTask<Ticket> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        return new(new Ticket("t-1", message));
    }
}

internal sealed class Remember(string id, Ledger ledger) : Executor<string, string>(id)
{
    public const string Secret = "SECRET-123-4567";

    public override async ValueTask<string> HandleAsync(string message, AfContext context, CancellationToken cancellationToken)
    {
        ledger.Ran(Id);
        await context.QueueStateUpdateAsync("last", new Ticket("s-1", "note: " + message + " " + Secret), cancellationToken);
        return message;
    }
}

internal static class TestWorkflows
{
    /// <summary>a > b > c > done: four supersteps, each with one executor.</summary>
    public static Workflow Chain(Ledger ledger)
    {
        ExecutorBinding a = new Step("a", ledger, "").BindExecutor();
        ExecutorBinding b = new Step("b", ledger, "").BindExecutor();
        ExecutorBinding c = new Step("c", ledger, "").BindExecutor();
        ExecutorBinding done = new Finish("done", ledger).BindExecutor();
        return new WorkflowBuilder(a).AddEdge(a, b).AddEdge(b, c).AddEdge(c, done).WithOutputFrom(done).Build();
    }

    /// <summary>a > b(fails while ledger.Failures > 0) > done.</summary>
    public static Workflow Flaky(Ledger ledger)
    {
        ExecutorBinding a = new Step("a", ledger, "").BindExecutor();
        ExecutorBinding b = new Step("b", ledger, "!boom").BindExecutor();
        ExecutorBinding done = new Finish("done", ledger).BindExecutor();
        return new WorkflowBuilder(a).AddEdge(a, b).AddEdge(b, done).WithOutputFrom(done).Build();
    }

    /// <summary>split > (left, right) > both: two executors in one superstep.</summary>
    public static Workflow FanOut(Ledger ledger)
    {
        ExecutorBinding split = new Split("split", ledger).BindExecutor();
        ExecutorBinding left = new Step("left", ledger, "").BindExecutor();
        ExecutorBinding right = new Step("right", ledger, "").BindExecutor();
        ExecutorBinding done = new Finish("done", ledger).BindExecutor();
        return new WorkflowBuilder(split).AddFanOutEdge(split, [left, right]).AddEdge(left, done).AddEdge(right, done).WithOutputFrom(done).Build();
    }

    /// <summary>prepare > port "approve" (Ticket in, bool out) > conclude.</summary>
    public static Workflow Approval(Ledger ledger)
    {
        ExecutorBinding prepare = new Prepare("prepare", ledger).BindExecutor();
        ExecutorBinding gate = RequestPort.Create<Ticket, bool>("approve").BindAsExecutor();
        ExecutorBinding conclude = new Conclude("conclude", ledger).BindExecutor();
        return new WorkflowBuilder(prepare).AddEdge(prepare, gate).AddEdge(gate, conclude).WithOutputFrom(conclude).Build();
    }

    /// <summary>prepare > port "ask" (Ticket in, string out) > concludeText: answered by signal.</summary>
    public static Workflow Question(Ledger ledger)
    {
        ExecutorBinding prepare = new Prepare("prepare", ledger).BindExecutor();
        ExecutorBinding gate = RequestPort.Create<Ticket, string>("ask").BindAsExecutor();
        ExecutorBinding conclude = new ConcludeText("conclude", ledger).BindExecutor();
        return new WorkflowBuilder(prepare).AddEdge(prepare, gate).AddEdge(gate, conclude).WithOutputFrom(conclude).Build();
    }

    /// <summary>ask the durable agent "writer" > done.</summary>
    public static Workflow WithAgent(Ledger ledger, DurableAgents agents, string agent = "writer")
    {
        ExecutorBinding ask = new Ask("ask", ledger, agents, agent).BindExecutor();
        ExecutorBinding done = new Finish("done", ledger).BindExecutor();
        return new WorkflowBuilder(ask).AddEdge(ask, done).WithOutputFrom(done).Build();
    }

    /// <summary>ask the durable agent "writer" > step b (fails while ledger.Failures > 0) > done: the agent call and the failing executor are in different executors.</summary>
    public static Workflow AgentThenFlaky(Ledger ledger, DurableAgents agents)
    {
        ExecutorBinding ask = new Ask("ask", ledger, agents, "writer").BindExecutor();
        ExecutorBinding b = new Step("b", ledger, "!boom").BindExecutor();
        ExecutorBinding done = new Finish("done", ledger).BindExecutor();
        return new WorkflowBuilder(ask).AddEdge(ask, b).AddEdge(b, done).WithOutputFrom(done).Build();
    }

    /// <summary>ask the durable agent "writer" > port "ask" (a person answers) > conclude.</summary>
    public static Workflow AgentThenQuestion(Ledger ledger, DurableAgents agents, string agent = "writer")
    {
        ExecutorBinding write = new AskThenTicket("write", ledger, agents, agent).BindExecutor();
        ExecutorBinding gate = RequestPort.Create<Ticket, string>("ask").BindAsExecutor();
        ExecutorBinding conclude = new ConcludeText("conclude", ledger).BindExecutor();
        return new WorkflowBuilder(write).AddEdge(write, gate).AddEdge(gate, conclude).WithOutputFrom(conclude).Build();
    }

    /// <summary>split > (gate "ask" answered by signal, and a chain of five steps) > both end in an output: the chain runs on while the request waits.</summary>
    public static Workflow Parallel(Ledger ledger)
    {
        ExecutorBinding split = new Split("split", ledger).BindExecutor();
        ExecutorBinding toTicket = new ToTicket("to-ticket", ledger).BindExecutor();
        ExecutorBinding gate = RequestPort.Create<Ticket, string>("ask").BindAsExecutor();
        ExecutorBinding conclude = new ConcludeText("conclude", ledger).BindExecutor();
        var builder = new WorkflowBuilder(split).AddEdge(split, toTicket).AddEdge(toTicket, gate).AddEdge(gate, conclude).WithOutputFrom(conclude);
        ExecutorBinding previous = split;
        for (int i = 1; i <= 5; i++)
        {
            ExecutorBinding next = new Step("c" + i, ledger, "").BindExecutor();
            builder.AddEdge(previous, next);
            previous = next;
        }

        ExecutorBinding end = new Finish("end", ledger).BindExecutor();
        return builder.AddEdge(previous, end).WithOutputFrom(end).Build();
    }

    /// <summary>split > (a ticket for a person to approve, and a chain of two steps) > both end in an output: a fan-out, a decision in the middle.</summary>
    public static Workflow FanOutWithDecision(Ledger ledger)
    {
        ExecutorBinding split = new Split("split", ledger).BindExecutor();
        ExecutorBinding toTicket = new Prepare("prepare", ledger).BindExecutor();
        ExecutorBinding gate = RequestPort.Create<Ticket, bool>("approve").BindAsExecutor();
        ExecutorBinding conclude = new Conclude("conclude", ledger).BindExecutor();
        ExecutorBinding c1 = new Step("c1", ledger, "").BindExecutor();
        ExecutorBinding c2 = new Step("c2", ledger, "").BindExecutor();
        ExecutorBinding end = new Finish("end", ledger).BindExecutor();
        return new WorkflowBuilder(split).AddEdge(split, toTicket).AddEdge(toTicket, gate).AddEdge(gate, conclude)
            .AddEdge(split, c1).AddEdge(c1, c2).AddEdge(c2, end).WithOutputFrom(conclude, end).Build();
    }

    /// <summary>The same as <see cref="Question"/> with one more executor between the port and the end: a changed structure.</summary>
    public static Workflow QuestionWithAudit(Ledger ledger)
    {
        ExecutorBinding prepare = new Prepare("prepare", ledger).BindExecutor();
        ExecutorBinding gate = RequestPort.Create<Ticket, string>("ask").BindAsExecutor();
        ExecutorBinding audit = new Step("audit", ledger, "").BindExecutor();
        ExecutorBinding conclude = new ConcludeText("conclude", ledger).BindExecutor();
        return new WorkflowBuilder(prepare).AddEdge(prepare, gate).AddEdge(gate, audit).AddEdge(audit, conclude).WithOutputFrom(conclude).Build();
    }

    /// <summary>A workflow whose executor ids change every time it is built.</summary>
    public static Workflow Unstable(Ledger ledger)
    {
        ExecutorBinding a = new Step("a-" + Guid.NewGuid().ToString("N"), ledger, "").BindExecutor();
        ExecutorBinding done = new Finish("done", ledger).BindExecutor();
        return new WorkflowBuilder(a).AddEdge(a, done).WithOutputFrom(done).Build();
    }

    /// <summary>remember (executor state with text) > done.</summary>
    public static Workflow Stateful(Ledger ledger)
    {
        ExecutorBinding remember = new Remember("remember", ledger).BindExecutor();
        ExecutorBinding done = new Finish("done", ledger).BindExecutor();
        return new WorkflowBuilder(remember).AddEdge(remember, done).WithOutputFrom(done).Build();
    }
}
