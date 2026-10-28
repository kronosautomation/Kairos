using System.Text;
using Kairos;
using Kairos.Samples.GettingStarted;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Usage:
//   dotnet run -- --provision "<admin connection string>" "<password for the kairos_dataplane role>"   (once per database)
//   dotnet run                                                                                           (ConnectionStrings:Kairos)
if (args.Length == 3 && args[0] == "--provision")
{
    await Database.ProvisionAsync(args[1], args[2]);
    Console.WriteLine("Roles and schema are in place.");
    return 0;
}

#region host
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("Kairos")
    ?? throw new InvalidOperationException("Set ConnectionStrings:Kairos to the kairos_dataplane connection string.");

builder.Services.AddKairos(options => options
    .UsePostgres(connectionString)
    .AllowUnencryptedPayloads() // getting started only: real data needs UsePayloadProtector (see Payload encryption)
    .AddOrderProcessing(new ConsoleOrderServices()));

using IHost host = builder.Build();
await host.StartAsync();
#endregion

#region start
KairosClient client = host.Services.GetRequiredService<KairosClient>();

// The workflow id makes starting idempotent: run this program twice and the second start finds the first run.
StartRunResult start = await client.StartAsync(
    KairosOptions.DefaultScope, "order-1001", OrderProcessing.WorkflowName, Encoding.UTF8.GetBytes("order-1001"));
Console.WriteLine(start.Started ? "Started the run." : "The run already exists; continuing it.");

var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);
while (await client.GetStateAsync(run) == RunState.Running)
{
    await Task.Delay(200);
}

IReadOnlyList<HistoryEvent> history = await client.GetHistoryAsync(run);
Console.WriteLine(Encoding.UTF8.GetString(((WorkflowCompleted)history[^1]).Result));
#endregion

await host.StopAsync();
return 0;

/// <summary>Prints each step so a restart is easy to see.</summary>
internal sealed class ConsoleOrderServices : IOrderServices
{
    public Task<string> ChargeAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Charging {orderId} (idempotency key {idempotencyKey})");
        return Task.FromResult("rcpt-" + orderId);
    }

    public Task ShipAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Shipping {orderId}");
        return Task.CompletedTask;
    }
}
