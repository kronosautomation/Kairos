using System.Text;
using Kairos;
using Kairos.Samples.GettingStarted;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Kairos.Samples.GettingStarted.Tests;

/// <summary>
/// The Getting started tutorial runs for real: the same workflow, registration and start code the docs quote, against the in-memory
/// store (every commit) and against PostgreSQL in a container (integration). If the sample stops compiling or stops behaving as the
/// page says, this fails the build.
/// </summary>
public sealed class OrderProcessingTests
{
    /// <summary>Counts what the outside world was asked to do, so "ran once across a restart" is a fact, not a hope.</summary>
    private sealed class RecordingServices : IOrderServices
    {
        private readonly List<string> _charges = [];
        private readonly List<string> _shipments = [];

        public IReadOnlyList<string> Charges { get { lock (_charges) { return [.. _charges]; } } }

        public IReadOnlyList<string> Shipments { get { lock (_shipments) { return [.. _shipments]; } } }

        public Task<string> ChargeAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken)
        {
            lock (_charges)
            {
                _charges.Add(idempotencyKey);
            }

            return Task.FromResult("rcpt-" + orderId);
        }

        public Task ShipAsync(string orderId, string idempotencyKey, CancellationToken cancellationToken)
        {
            lock (_shipments)
            {
                _shipments.Add(idempotencyKey);
            }

            return Task.CompletedTask;
        }
    }

    private static IHost BuildHost(Action<KairosOptions> useStore, RecordingServices services)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(options =>
        {
            options.AllowUnencryptedPayloads();
            useStore(options);
            options.PollFallbackInterval = TimeSpan.FromMilliseconds(50);
            options.AddOrderProcessing(services);
        });
        return builder.Build();
    }

    private static async Task<RunState> WaitForCloseAsync(KairosClient client, RunKey run)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            RunState? state = await client.GetStateAsync(run);
            if (state is { } s && s != RunState.Running)
            {
                return s;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The run did not finish within 30 seconds.");
    }

    /// <summary>
    /// Waits until the charge result is part of the run's history. Stopping earlier is also safe, but the interrupted charge would be
    /// retried after its lease expires (a minute), with the same idempotency key.
    /// </summary>
    private static async Task WaitForChargeToBeRecordedAsync(KairosClient client, RunKey run)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await client.GetHistoryAsync(run)).Any(e => e is TimerStarted))
        {
            Assert.True(DateTime.UtcNow < deadline, "The charge was never recorded.");
            await Task.Delay(20);
        }
    }

    private static async Task<(KairosClient Client, RunKey Run)> StartOrderAsync(IHost host)
    {
        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(
            KairosOptions.DefaultScope, "order-1001", OrderProcessing.WorkflowName, Encoding.UTF8.GetBytes("order-1001"));
        return (client, new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId));
    }

    [Fact]
    public async Task The_order_is_charged_then_shipped_and_the_run_completes()
    {
        var services = new RecordingServices();
        using IHost host = BuildHost(o => o.UseStore(new InMemoryKairosStore()), services);
        await host.StartAsync();

        (KairosClient client, RunKey run) = await StartOrderAsync(host);

        Assert.Equal(RunState.Completed, await WaitForCloseAsync(client, run));
        Assert.Equal("order-1001: shipped, receipt rcpt-order-1001", Encoding.UTF8.GetString(((WorkflowCompleted)(await client.GetHistoryAsync(run))[^1]).Result));
        Assert.Single(services.Charges);
        Assert.Single(services.Shipments);
        await host.StopAsync();
    }

    [Fact]
    public async Task Stopping_the_process_during_the_wait_and_starting_it_again_finishes_the_run_without_charging_twice()
    {
        var store = new InMemoryKairosStore(); // stands in for the database: it outlives the process
        var services = new RecordingServices();
        RunKey run;

        using (IHost first = BuildHost(o => o.UseStore(store), services))
        {
            await first.StartAsync();
            (KairosClient client, run) = await StartOrderAsync(first);
            await WaitForChargeToBeRecordedAsync(client, run); // the charge is in the run's history; the two-second wait has begun

            await first.StopAsync(); // "crash"
        }

        using IHost second = BuildHost(o => o.UseStore(store), services); // restart
        await second.StartAsync();
        var restarted = second.Services.GetRequiredService<KairosClient>();

        Assert.Equal(RunState.Completed, await WaitForCloseAsync(restarted, run));
        Assert.Single(services.Charges);   // not charged again
        Assert.Single(services.Shipments); // shipped once
        await second.StopAsync();
    }

    [Fact]
    public async Task Starting_the_same_order_twice_finds_the_existing_run()
    {
        var services = new RecordingServices();
        using IHost host = BuildHost(o => o.UseStore(new InMemoryKairosStore()), services);
        await host.StartAsync();
        var client = host.Services.GetRequiredService<KairosClient>();

        StartRunResult first = await client.StartAsync(KairosOptions.DefaultScope, "order-1001", OrderProcessing.WorkflowName, Encoding.UTF8.GetBytes("order-1001"));
        StartRunResult second = await client.StartAsync(KairosOptions.DefaultScope, "order-1001", OrderProcessing.WorkflowName, Encoding.UTF8.GetBytes("order-1001"));

        Assert.True(first.Started);
        Assert.False(second.Started);
        Assert.Equal(first.RunId, second.RunId);
        await host.StopAsync();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task On_postgres_provisioning_then_running_then_restarting_works_as_the_tutorial_says()
    {
        await using PostgreSqlContainer container = new PostgreSqlBuilder(Environment.GetEnvironmentVariable("KAIROS_PG_IMAGE") ?? "postgres:17").Build();
        await container.StartAsync();

        // Step "set up the database" from the tutorial.
        await Database.ProvisionAsync(container.GetConnectionString(), "dataplane-sample");
        var appConnection = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Username = "kairos_dataplane",
            Password = "dataplane-sample",
        };

        var services = new RecordingServices();
        RunKey run;
        using (IHost first = BuildHost(o => o.UsePostgres(appConnection.ConnectionString), services))
        {
            await first.StartAsync();
            (KairosClient client, run) = await StartOrderAsync(first);
            await WaitForChargeToBeRecordedAsync(client, run);

            await first.StopAsync();
        }

        using IHost second = BuildHost(o => o.UsePostgres(appConnection.ConnectionString), services);
        await second.StartAsync();
        var restarted = second.Services.GetRequiredService<KairosClient>();

        Assert.Equal(RunState.Completed, await WaitForCloseAsync(restarted, run));
        Assert.Single(services.Charges);
        Assert.Single(services.Shipments);
        await second.StopAsync();
    }
}
