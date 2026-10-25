using System.Text;

namespace Kairos.Samples.Aspire.Orders;

/// <summary>A small order workflow: charge, wait a moment for a cancellation, ship. Every step is a span in the run's trace.</summary>
public static class OrderWorkflow
{
    public const string Name = "process-order";

    #region workflow
    public static async Task<byte[]> RunAsync(IWorkflowContext ctx, byte[] input)
    {
        string orderId = Encoding.UTF8.GetString(input);
        byte[] receipt = await ctx.ExecuteActivityAsync("charge-card", input, new RetryPolicy { MaximumAttempts = 5 });
        await ctx.DelayAsync(TimeSpan.FromSeconds(3)); // a durable timer: it appears as its own span, as long as it was pending
        await ctx.ExecuteActivityAsync("ship-order", input);
        return Encoding.UTF8.GetBytes($"{orderId}: shipped, receipt {Encoding.UTF8.GetString(receipt)}");
    }
    #endregion

    /// <summary>Registers the workflow and its two activities.</summary>
    public static KairosOptions AddOrders(this KairosOptions options) => options
        .AddWorkflow(Name, RunAsync)
        .AddActivity("charge-card", (context, input, _) =>
            Task.FromResult(Encoding.UTF8.GetBytes($"rcpt-{Encoding.UTF8.GetString(input)}-{context.IdempotencyKey[^6..]}")))
        .AddActivity("ship-order", (_, input, _) => Task.FromResult(input));
}
