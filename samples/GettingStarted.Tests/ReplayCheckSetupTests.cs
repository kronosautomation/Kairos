using Kairos;
using Kairos.Samples.GettingStarted;

namespace Kairos.Samples.GettingStarted.Tests;

public sealed class ReplayCheckSetupTests
{
    [Fact]
    public void The_setup_registers_the_order_workflow_for_the_default_build()
    {
        var options = new KairosOptions();

        new ReplayCheckSetup().Configure(options);

        Assert.True(options.WorkflowRegistry.TryGet(OrderProcessing.WorkflowName, KairosClient.DefaultBuildId, out _));
    }
}
