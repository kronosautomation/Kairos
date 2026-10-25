using System.Diagnostics;
using System.Text;
using Kairos.Samples.Aspire.Orders;
using Kairos.Testing;

namespace Kairos.Samples.Aspire.Orders.Tests;

public sealed class OrderWorkflowTests
{
    [Fact]
    public async Task The_order_workflow_charges_waits_and_ships_and_its_spans_share_one_trace()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == KairosTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span =>
            {
                lock (spans)
                {
                    spans.Add(span);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);

        var engine = new TestEngine();
        engine.RegisterWorkflow(OrderWorkflow.Name, OrderWorkflow.RunAsync);
        engine.RegisterActivity("charge-card", input => Task.FromResult(Encoding.UTF8.GetBytes("rcpt-" + Encoding.UTF8.GetString(input))));
        engine.RegisterActivity("ship-order", input => Task.FromResult(input));
        Guid run = engine.StartWorkflow(OrderWorkflow.Name, Encoding.UTF8.GetBytes("1001"), Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(RunState.Completed, await engine.RunAsync(run));

        var result = (WorkflowCompleted)engine.GetHistory(run)[^1];
        Assert.Equal("1001: shipped, receipt rcpt-1001", Encoding.UTF8.GetString(result.Result));
        lock (spans)
        {
            Assert.Contains(spans, s => s.OperationName == "kairos.timer");
            Assert.Single(spans.Select(s => s.TraceId).Distinct());
        }
    }
}
