using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Kairos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kairos.Samples.Operations.Tests;

internal sealed class Capture : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Sink(Lines);

    public void Dispose()
    {
    }

    private sealed class Sink(ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}

/// <summary>The claims the operations pages make about migrations, several instances, shutdown and the hardening setup, run against PostgreSQL.</summary>
[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ProductionTests(PostgresFixture fixture)
{
    private static readonly IReadOnlyDictionary<string, byte[]> KeyRing = new Dictionary<string, byte[]> { ["ring-1"] = RandomNumberGenerator.GetBytes(32) };

    private static EnvelopePayloadProtector Protector() => new(new LocalKeyProvider(KeyRing, "ring-1"));


    private static IHost Build(Action<KairosOptions> configure, Capture? capture = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        if (capture is not null)
        {
            builder.Logging.AddProvider(capture);
        }

        builder.Services.AddKairos(o =>
        {
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            configure(o);
        });
        return builder.Build();
    }

    private static async Task<RunState?> WaitForAsync(KairosClient client, RunKey run, Func<RunState?, bool> done, TimeSpan limit)
    {
        DateTime deadline = DateTime.UtcNow + limit;
        RunState? state = null;
        while (DateTime.UtcNow < deadline)
        {
            state = await client.GetStateAsync(run);
            if (done(state))
            {
                break;
            }

            await Task.Delay(50);
        }

        return state;
    }

    private static async Task<RunKey> StartAsync(KairosClient client, string workflowType, string id)
    {
        StartRunResult started = await client.StartAsync(KairosOptions.DefaultScope, id, workflowType, Encoding.UTF8.GetBytes("hello"));
        return new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, started.RunId);
    }

    [Fact]
    public async Task The_migrate_on_start_sample_migrates_an_empty_database_and_the_application_runs_as_the_data_plane_role()
    {
        string database = await fixture.NewDatabaseAsync(migrate: false);
        try
        {
            using IHost host = Build(o => ProductionSetup.MigrateAtStart(
                    o, fixture.Connection(database, PostgresProvisioning.DataPlaneRole), fixture.Connection(database, PostgresProvisioning.MigratorRole))
                .UsePayloadProtector(Protector())
                .AddWorkflow("echo", (_, input) => Task.FromResult(input)));
            await host.StartAsync();

            Assert.True(await fixture.ScalarAsync(database, "SELECT count(*) FROM kairos.schema_migrations") > 0);
            IDatabaseRoleReport role = host.Services.GetRequiredService<IDatabaseRoleReport>();
            Assert.Equal(PostgresProvisioning.DataPlaneRole, role.Inspection!.Role);
            Assert.False(role.Inspection.Privileged);
            await host.StopAsync();
        }
        finally
        {
            await fixture.DropAsync(database);
        }
    }

    [Fact]
    public async Task A_client_only_instance_starts_runs_and_runs_no_work_until_an_instance_with_workers_joins()
    {
        string database = await fixture.NewDatabaseAsync(migrate: true);
        string connection = fixture.Connection(database, PostgresProvisioning.DataPlaneRole);
        try
        {
            using IHost clientOnly = Build(o => ProductionSetup.ClientOnly(o, connection, Protector()));
            await clientOnly.StartAsync();
            KairosClient client = clientOnly.Services.GetRequiredService<KairosClient>();
            RunKey run = await StartAsync(client, "echo", "client-only-1");

            await Task.Delay(TimeSpan.FromSeconds(2));
            Assert.Equal(RunState.Running, await client.GetStateAsync(run)); // nothing here runs workflow code

            HealthReport health = await clientOnly.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
            Assert.Equal(HealthStatus.Healthy, health.Status); // a process with no worker loops is not reported as stalled
            Assert.Null(clientOnly.Services.GetRequiredService<StuckTaskMonitor>().Backlog); // the queue and stuck-run gauges are fed by a worker loop, so this instance reports none

            using IHost worker = Build(o => o.UsePostgres(connection).UsePayloadProtector(Protector()).AddWorkflow("echo", (_, input) => Task.FromResult(input)));
            await worker.StartAsync();
            Assert.Equal(RunState.Completed, await WaitForAsync(client, run, s => s is RunState.Completed, TimeSpan.FromSeconds(30)));
            Assert.NotNull(worker.Services.GetRequiredService<StuckTaskMonitor>().Backlog);

            await worker.StopAsync();
            await clientOnly.StopAsync();
        }
        finally
        {
            await fixture.DropAsync(database);
        }
    }

    [Fact]
    public async Task A_stopping_instance_gives_its_activity_back_at_once_and_the_next_attempt_has_the_same_idempotency_key()
    {
        string database = await fixture.NewDatabaseAsync(migrate: true);
        string connection = fixture.Connection(database, PostgresProvisioning.DataPlaneRole);
        try
        {
            var firstKey = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondKey = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            WorkflowFunc workflow = async (ctx, input) => await ctx.ExecuteActivityAsync("slow", input);

            using IHost first = Build(o => o
                .UsePostgres(connection).UsePayloadProtector(Protector())
                .AddWorkflow("job", workflow)
                .AddActivity("slow", async (context, _, ct) =>
                {
                    firstKey.TrySetResult(context.IdempotencyKey);
                    await Task.Delay(Timeout.Infinite, ct); // an activity that is still running when the host is told to stop
                    return [];
                }));
            await first.StartAsync();
            KairosClient client = first.Services.GetRequiredService<KairosClient>();
            RunKey run = await StartAsync(client, "job", "drain-1");
            string key = await firstKey.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await first.StopAsync(); // far sooner than the one-minute activity lease

            using IHost second = Build(o => o
                .UsePostgres(connection).UsePayloadProtector(Protector())
                .AddWorkflow("job", workflow)
                .AddActivity("slow", (context, input, _) =>
                {
                    secondKey.TrySetResult(context.IdempotencyKey);
                    return Task.FromResult(input);
                }));
            await second.StartAsync();
            Assert.Equal(RunState.Completed, await WaitForAsync(second.Services.GetRequiredService<KairosClient>(), run, s => s is RunState.Completed, TimeSpan.FromSeconds(20)));
            Assert.Equal(key, await secondKey.Task);
            await second.StopAsync();
        }
        finally
        {
            await fixture.DropAsync(database);
        }
    }

    [Fact]
    public async Task One_instance_holds_one_connection_per_task_loop_plus_the_listener_when_idle()
    {
        string database = await fixture.NewDatabaseAsync(migrate: true);
        try
        {
            using IHost host = Build(o =>
            {
                ProductionSetup.WithPoolSize(o, fixture.Connection(database, PostgresProvisioning.DataPlaneRole), maximumConnections: 20).AllowUnencryptedPayloads();
                o.WorkerCount = 3;
            });
            await host.StartAsync();
            await Task.Delay(TimeSpan.FromSeconds(2));

            long sessions = await fixture.ScalarAsync(
                database, $"SELECT count(*) FROM pg_stat_activity WHERE datname = '{database}' AND usename = '{PostgresProvisioning.DataPlaneRole}'");
            Assert.InRange(sessions, 1, 3 + 2); // at most one per loop, one that listens, and the connection a check just used
            await host.StopAsync();
        }
        finally
        {
            await fixture.DropAsync(database);
        }
    }

    [Fact]
    public async Task The_hardened_setup_starts_as_the_data_plane_role_and_refuses_an_administrator_login()
    {
        string database = await fixture.NewDatabaseAsync(migrate: true);
        try
        {
            var signer = Kairos.Agents.LocalApprovalEvidenceSigner.Create("ops-1");
            var verifier = new Kairos.Agents.EsApproverVerifier(new Dictionary<string, byte[]> { ["ops-1"] = signer.PublicKey });
            IKeyProvider keys = new LocalKeyProvider(KeyRing, "ring-1");

            HostApplicationBuilder good = Host.CreateApplicationBuilder();
            good.Logging.ClearProviders();
            ProductionSetup.AddHardenedKairos(good.Services, fixture.Connection(database, PostgresProvisioning.DataPlaneRole), keys, verifier, text => text.Replace("jane@example.com", "[email]", StringComparison.Ordinal));
            using IHost host = good.Build();
            await host.StartAsync();
            Assert.False(host.Services.GetRequiredService<KairosOptions>().UnencryptedPayloadsAllowed);
            Assert.NotNull(host.Services.GetRequiredService<KairosOptions>().PayloadProtector);
            await host.StopAsync();

            HostApplicationBuilder bad = Host.CreateApplicationBuilder();
            bad.Logging.ClearProviders();
            ProductionSetup.AddHardenedKairos(bad.Services, fixture.Connection(database, null), keys, verifier, text => text);
            using IHost refused = bad.Build();
            InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => refused.StartAsync());
            Assert.Contains("row level security", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.DropAsync(database);
        }
    }

    [Fact]
    public void The_tls_sample_asks_for_certificate_verification()
    {
        string changed = ProductionSetup.RequireTls("Host=db.example.com;Database=kairos;Username=kairos_dataplane");
        Assert.Contains("SSL Mode=VerifyFull", changed, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Claims that need no database: the signals the alert guidance names exist, and the plaintext warning is what the page says.</summary>
public sealed class SignalTests
{
    [Fact]
    public async Task Every_instrument_the_alert_guidance_names_is_published_by_the_Kairos_meter()
    {
        var names = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AlertSignals.MeterName)
            {
                names.Add(instrument.Name);
            }
        };

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o => o.UseStore(new InMemoryKairosStore()).AllowUnencryptedPayloads());
        using IHost host = builder.Build();
        _ = host.Services.GetRequiredService<StuckTaskMonitor>(); // the two gauges are created with the monitor
        _ = KairosMetrics.RunsClosed;
        listener.Start();
        await Task.CompletedTask;

        string[] expected =
        [
            AlertSignals.LeaseExpiries, AlertSignals.ActivityRetries, AlertSignals.ActivityAttempts, AlertSignals.ReplayFaults,
            AlertSignals.StoreRetries, AlertSignals.RunsClosed, AlertSignals.StuckWorkflowTasks, AlertSignals.QueueDepth,
        ];
        foreach (string name in expected)
        {
            Assert.Contains(name, names);
        }
    }

    [Fact]
    public async Task An_instance_that_has_not_registered_an_activity_fails_the_attempt_for_good_instead_of_retrying_it()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddKairos(o =>
        {
            o.UseStore(new InMemoryKairosStore()).AllowUnencryptedPayloads();
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(50);
            o.AddWorkflow("needs-charge", async (ctx, input) => await ctx.ExecuteActivityAsync("charge", input)); // no AddActivity("charge", ...) here
        });
        using IHost host = builder.Build();
        await host.StartAsync();
        KairosClient client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult started = await client.StartAsync(KairosOptions.DefaultScope, "x", "needs-charge", [1]);
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, started.RunId);

        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        RunState? state = null;
        while (DateTime.UtcNow < deadline && state is not RunState.Failed)
        {
            await Task.Delay(50);
            state = await client.GetStateAsync(run);
        }

        Assert.Equal(RunState.Failed, state);
        ActivityFailed failure = (await client.GetHistoryAsync(run)).OfType<ActivityFailed>().Single();
        Assert.False(failure.WillRetry);
        Assert.Equal("ActivityNotRegisteredException", failure.ErrorType);
        await host.StopAsync();
    }

    [Fact]
    public async Task Switching_encryption_off_writes_the_warning_the_page_quotes_at_every_start()
    {
        var capture = new Capture();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        builder.Services.AddKairos(o => o.UseStore(new InMemoryKairosStore()).AllowUnencryptedPayloads());
        using IHost host = builder.Build();
        await host.StartAsync();
        await Task.Delay(200);
        await host.StopAsync();

        Assert.Contains(capture.Lines, line => line.StartsWith("Warning: " + AlertSignals.PlaintextWarning, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_call_to_the_key_provider_is_counted_with_succeeded_false()
    {
        var measured = new ConcurrentBag<(string Kind, bool Succeeded)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KeyUseMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string kind = "";
            bool ok = true;
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "kind")
                {
                    kind = (string)tag.Value!;
                }
                else if (tag.Key == "succeeded")
                {
                    ok = (bool)tag.Value!;
                }
            }

            measured.Add((kind, ok));
        });
        listener.Start();

        using var protector = new EnvelopePayloadProtector(new RefusingKeyProvider(), TimeProvider.System, new EnvelopeOptions { KeyUseObserver = new KeyUseMetrics() });
        await Assert.ThrowsAnyAsync<Exception>(async () => await protector.ProtectAsync(KairosOptions.DefaultScope, new byte[] { 1 }, new byte[] { 2 }, CancellationToken.None));

        Assert.Contains(("Wrap", false), measured);
    }

    private sealed class RefusingKeyProvider : IKeyProvider
    {
        public ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The key service refused.");

        public ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The key service refused.");
    }
}
