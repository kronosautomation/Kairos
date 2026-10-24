using System.Text;

namespace Kairos.Samples.GettingStarted;

/// <summary>The outside world the order workflow talks to: a payment service and a warehouse.</summary>
public interface IOrderServices
{
    /// <summary>Charges the customer. A real payment API accepts the idempotency key and ignores a repeated charge.</summary>
    Task<string> ChargeAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken);

    Task ShipAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken);
}

public static class OrderProcessing
{
    public const string WorkflowName = "process-order";
    public const string ChargeActivity = "charge-card";
    public const string ShipActivity = "ship-order";

    #region workflow
    /// <summary>
    /// Charge the card, give the customer a short window to cancel, then ship. Each step is recorded as it completes, so if the
    /// process stops anywhere in here the run continues from the last recorded step, not from the beginning.
    /// </summary>
    public static async Task<byte[]> ProcessOrderAsync(IWorkflowContext ctx, byte[] input)
    {
        string orderId = Encoding.UTF8.GetString(input);

        byte[] receipt = await ctx.ExecuteActivityAsync(
            ChargeActivity, input, new RetryPolicy { MaximumAttempts = 5 });

        await ctx.DelayAsync(TimeSpan.FromSeconds(2)); // a durable timer: it survives a restart

        await ctx.ExecuteActivityAsync(ShipActivity, input);

        return Encoding.UTF8.GetBytes($"{orderId}: shipped, receipt {Encoding.UTF8.GetString(receipt)}");
    }
    #endregion

    #region activities
    private static async Task<byte[]> ChargeCardAsync(IOrderServices services, ActivityContext activity, byte[] input, CancellationToken cancellationToken)
    {
        string orderId = Encoding.UTF8.GetString(input);

        // The idempotency key is the same on every retry of this activity, so the payment service can recognise a repeat.
        string receipt = await services.ChargeAsync(orderId, activity.IdempotencyKey, cancellationToken);
        return Encoding.UTF8.GetBytes(receipt);
    }

    private static async Task<byte[]> ShipOrderAsync(IOrderServices services, ActivityContext activity, byte[] input, CancellationToken cancellationToken)
    {
        await services.ShipAsync(Encoding.UTF8.GetString(input), activity.IdempotencyKey, cancellationToken);
        return [];
    }
    #endregion

    #region registration
    /// <summary>Registers the workflow and its activities with Kairos.</summary>
    public static KairosOptions AddOrderProcessing(this KairosOptions options, IOrderServices services) => options
        .AddActivity(ChargeActivity, (activity, input, ct) => ChargeCardAsync(services, activity, input, ct))
        .AddActivity(ShipActivity, (activity, input, ct) => ShipOrderAsync(services, activity, input, ct))
        .AddWorkflow(WorkflowName, ProcessOrderAsync);
    #endregion
}
