using Kairos;
using Kairos.Agents;
using Kairos.Samples.AgentFramework;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Needs a database set up as in the GettingStarted sample (run its --provision once).
//   dotnet run        (ConnectionStrings:Kairos)
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("Kairos")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Kairos to the kairos_dataplane connection string.");

// The model here is a stand-in that asks for the tools a support agent would. Give the agent your provider's IChatClient instead.
builder.Services.AddSupportAgent(options => options.UsePostgres(connectionString).AllowUnencryptedPayloads(), new DemoModel(), new ConsoleOrders(), new TokenBudget(tokensPerDay: 100_000));

using IHost host = builder.Build();
await host.StartAsync();

Task<string> answer = SupportAgentSample.AskAsync(host.Services, "Please refund order A-1042.", CancellationToken.None);

// The refund waits for a person. Stop this program now and start it again: the run is in the database and carries on from the same place.
var client = host.Services.GetRequiredService<KairosClient>();
while (!answer.IsCompleted)
{
    foreach (RunSummary run in await host.Services.GetRequiredService<IKairosStore>().ListRunsAsync(KairosOptions.DefaultScope, new RunFilter(), 10, CancellationToken.None))
    {
        if (await SupportAgentSample.ApproveFirstAsync(host.Services, run.Run, new ApproverIdentity("user:alice", "Alice", ["finance"]), CancellationToken.None))
        {
            Console.WriteLine("Alice approved the refund.");
        }
    }

    await Task.Delay(200);
}

Console.WriteLine(await answer);
await host.StopAsync();
return 0;

/// <summary>Prints each call so a restart is easy to see.</summary>
internal sealed class ConsoleOrders : IOrders
{
    public Task<string> FindAsync(string orderId, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Looking up {orderId}");
        return Task.FromResult($"order {orderId}: shipped on Monday");
    }

    public Task<string> RefundAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Refunding {orderId} (idempotency key {idempotencyKey})");
        return Task.FromResult($"refunded {orderId}");
    }
}
