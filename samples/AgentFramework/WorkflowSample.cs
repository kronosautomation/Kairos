using Kairos.Agents;
using Kairos.Agents.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AgentFrameworkContext = Microsoft.Agents.AI.Workflows.IWorkflowContext;

namespace Kairos.Samples.AgentFramework;

/// <summary>A refund being reviewed.</summary>
public sealed record RefundCase(string OrderId, string Summary, decimal Amount);

/// <summary>The refunds service. It honours an idempotency key: asked twice with the same key, it refunds once.</summary>
public interface IRefunds
{
    Task RefundAsync(string orderId, decimal amount, string idempotencyKey, CancellationToken cancellationToken);
}

/// <summary>
/// An Agent Framework workflow made durable: a refund is summarised by an agent, a person in finance decides, and the refund is made. The workflow is written the usual way (executors, edges, a request port); registering it
/// is the only thing that mentions Kairos.
/// </summary>
public static class WorkflowSample
{
    #region workflow-executors
    /// <summary>Executors have explicit ids: a checkpoint names them. This one reads "A-1042 120" (an order and an amount).</summary>
    internal sealed class Intake() : Executor<string, RefundCase>("intake")
    {
        public override ValueTask<RefundCase> HandleAsync(string message, AgentFrameworkContext context, CancellationToken cancellationToken)
        {
            string[] parts = message.Split(' ', 2);
            return new(new RefundCase(parts[0], $"Refund request for order {parts[0]}", decimal.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Asks a durable agent to summarise the case. If the worker dies while this superstep runs, the superstep runs again and this call finds the answer the conversation already gave: the model is asked once.
    /// The case is kept in executor state for the executor that makes the refund.
    /// </summary>
    internal sealed class Summarize(DurableAgents agents) : Executor<RefundCase, RefundCase>("summarize")
    {
        public override async ValueTask<RefundCase> HandleAsync(RefundCase message, AgentFrameworkContext context, CancellationToken cancellationToken)
        {
            DurableAIAgent summarizer = agents.Get("summarizer");
            AgentResponse reply = await summarizer.RunAsync(message.Summary, await summarizer.CreateSessionAsync(cancellationToken), cancellationToken: cancellationToken);
            RefundCase summarised = message with { Summary = reply.Text };
            await context.QueueStateUpdateAsync("case", summarised, cancellationToken);
            return summarised;
        }
    }

    /// <summary>
    /// Makes the refund. An executor can run twice (after a crash, the superstep it was in runs again), so the refund goes to the service with a key that is the same both times: the service refunds once.
    /// </summary>
    [YieldsOutput(typeof(string))]
    internal sealed class Settle(IRefunds refunds) : Executor<bool>("settle")
    {
        public override async ValueTask HandleAsync(bool approved, AgentFrameworkContext context, CancellationToken cancellationToken)
        {
            RefundCase? refund = await context.ReadStateAsync<RefundCase>("case", cancellationToken);
            if (!approved || refund is null)
            {
                await context.YieldOutputAsync("declined", cancellationToken);
                return;
            }

            await refunds.RefundAsync(refund.OrderId, refund.Amount, AgentFrameworkWorkflowContext.IdempotencyKey(Id), cancellationToken);
            await context.YieldOutputAsync($"refunded {refund.Amount} for {refund.OrderId}", cancellationToken);
        }
    }
    #endregion

    #region workflow-build
    /// <summary>The graph: intake, summary, a request to finance, the refund. It is built again for every step, and must be the same graph every time.</summary>
    public static Workflow Build(WorkflowBuildContext context, IRefunds refunds)
    {
        ExecutorBinding intake = new Intake().BindExecutor();
        ExecutorBinding summarize = new Summarize(context.Agents).BindExecutor();
        ExecutorBinding finance = RequestPort.Create<RefundCase, bool>("finance-approval").BindAsExecutor();
        ExecutorBinding settle = new Settle(refunds).BindExecutor();
        return new WorkflowBuilder(intake).AddEdge(intake, summarize).AddEdge(summarize, finance).AddEdge(finance, settle).WithOutputFrom(settle).Build();
    }
    #endregion

    public static ChatClientAgent Summarizer(IChatClient model) =>
        new(model, new ChatClientAgentOptions { Name = "summarizer", Description = "Summarises a refund request.", ChatOptions = new ChatOptions { Instructions = "Summarise the refund request in one sentence." } });

    #region workflow-register
    /// <summary>
    /// Registers the workflow. The request port <c>finance-approval</c> becomes an approval: it appears in the approvals inbox with the text below, waits for a person from finance, and holds no worker while it waits.
    /// A port that is not registered as an approval is answered by signal (see <see cref="AskCustomerAsync"/>).
    /// </summary>
    public static IServiceCollection AddRefundReview(this IServiceCollection services, Action<KairosOptions> kairos, IChatClient summarizer, IRefunds refunds) =>
        services
            .AddKairos(kairos)
            .AddAgentFramework(af => af
                .AddAgent(Summarizer(summarizer))
                .AddWorkflow<string>(
                    "refund-review", context => Build(context, refunds),
                    workflow => workflow.RequestsAsApprovals(
                        "finance-approval", new ApprovalRequirement(Roles: ["finance"]),
                        request => new ApprovalDescription($"Refund {request.As<RefundCase>()!.Amount} for order {request.As<RefundCase>()!.OrderId}?"))));
    #endregion

    #region workflow-use
    /// <summary>Starts a review and waits for it to finish. It can take days (finance has to decide); nothing runs and nothing is held while it waits, and this call may be made again from another process.</summary>
    public static async Task<string> ReviewAsync(IServiceProvider services, string order, CancellationToken cancellationToken)
    {
        DurableWorkflow workflow = services.GetRequiredService<DurableWorkflows>().Get("refund-review");
        DurableWorkflowRun run = await workflow.StartAsync(order, cancellationToken: cancellationToken);

        WorkflowView view = await run.WaitForCompletionAsync(cancellationToken);
        return view.Status == DurableWorkflowStatus.Completed ? view.Outputs[0].As<string>()! : $"did not finish: {view.Status}";
    }
    #endregion

    #region workflow-inbox
    /// <summary>
    /// Finance's side: the open requests of a workflow are in its view. An approval is decided in the approvals inbox like any other (the person is whoever your application authenticated); the run to decide in is
    /// <see cref="DurableWorkflowRun.Run"/>.
    /// </summary>
    public static async Task<bool> ApproveAsync(IServiceProvider services, DurableWorkflowRun run, ApproverIdentity approver, CancellationToken cancellationToken)
    {
        WorkflowView view = await run.WaitForRequestsAsync(cancellationToken);
        return view.Pending.Any(r => r.IsApproval) && await SupportAgentSample.ApproveFirstAsync(services, run.Run, approver, cancellationToken);
    }
    #endregion

    #region workflow-signal
    /// <summary>A request through a port that is not an approval is answered with a call: here, a customer's reply.</summary>
    public static async Task<string> AskCustomerAsync(IServiceProvider services, string question, string reply, CancellationToken cancellationToken)
    {
        DurableWorkflowRun run = await services.GetRequiredService<DurableWorkflows>().Get("ask-customer").StartAsync(question, cancellationToken: cancellationToken);
        WorkflowView waiting = await run.WaitForRequestsAsync(cancellationToken);

        await run.RespondAsync(waiting.Pending[0].RequestId, reply, cancellationToken); // asked twice, answered once

        WorkflowView done = await run.WaitForCompletionAsync(cancellationToken);
        return done.Outputs[0].As<string>()!;
    }
    #endregion

    #region workflow-stop
    /// <summary>
    /// Stops a workflow. A cancel is noticed at the next superstep (the one in flight finishes and is recorded) and at once if the workflow is waiting for a request; the conversations its executors started stop with it.
    /// Terminating is for an operator, with a verified principal who is named in the audit trail.
    /// </summary>
    public static async Task StopAsync(DurableWorkflowRun run, Principal operatorOnCall, bool terminate, CancellationToken cancellationToken)
    {
        if (terminate)
        {
            await run.TerminateAsync(operatorOnCall, "stuck", cancellationToken);
        }
        else
        {
            await run.CancelAsync("the customer withdrew the request", cancellationToken);
        }
    }
    #endregion

    #region workflow-ask-executors
    /// <summary>Turns the question into the request the port raises. The answer comes back as the port's response, and the workflow waits for it holding nothing.</summary>
    internal sealed class Prepare() : Executor<string, RefundCase>("prepare")
    {
        public override ValueTask<RefundCase> HandleAsync(string message, AgentFrameworkContext context, CancellationToken cancellationToken) => new(new RefundCase("-", message, 0));
    }

    [YieldsOutput(typeof(string))]
    internal sealed class Conclude() : Executor<string>("conclude")
    {
        public override async ValueTask HandleAsync(string message, AgentFrameworkContext context, CancellationToken cancellationToken) =>
            await context.YieldOutputAsync($"the customer said: {message}", cancellationToken);
    }

    public static Workflow BuildAskCustomer()
    {
        ExecutorBinding prepare = new Prepare().BindExecutor();
        ExecutorBinding reply = RequestPort.Create<RefundCase, string>("customer-reply").BindAsExecutor();
        ExecutorBinding conclude = new Conclude().BindExecutor();
        return new WorkflowBuilder(prepare).AddEdge(prepare, reply).AddEdge(reply, conclude).WithOutputFrom(conclude).Build();
    }
    #endregion

    /// <summary>Registers the workflow whose request is answered by signal.</summary>
    public static IServiceCollection AddAskCustomer(this IServiceCollection services, Action<KairosOptions> kairos) =>
        services
            .AddKairos(kairos)
            .AddAgentFramework(af => af.AddWorkflow<string>("ask-customer", _ => BuildAskCustomer()));
}
