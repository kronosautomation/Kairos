using System.Text;
using Kairos.Cloud.SelfHosted;
using Kairos.Persistence.Postgres;
using Kairos.Testing;
using Kairos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kairos.Persistence.Tests;

/// <summary>
/// I7 against real storage. With client-side encryption on, known plaintext canaries pushed through every payload path (start input,
/// activity input and result, signal, workflow result, error text) must be absent from every byte of every table, and from the logs.
/// The control run without encryption proves the scan can find them.
/// </summary>
[Collection(PostgresTestGroup.Name)]
[Trait("Category", "Integration")]
public sealed class ConfidentialityTests(PostgresFixture fixture)
{
    private const string Canary = "CANARY-SSN-078-05-1120-XYZZY";
    private static readonly TenantScope Scope = new(Guid.Parse("cccccccc-0000-0000-0000-00000000000c"), Guid.Parse("cccccccc-0000-0000-0000-0000000000cc"));

    private static WorkflowRegistry Workflows() => new WorkflowRegistry().Register("wf", "default", async (ctx, input) =>
    {
        byte[] charged = await ctx.ExecuteActivityAsync("charge", input);
        byte[] approval = await ctx.WaitForSignalAsync("approve");
        try
        {
            await ctx.ExecuteActivityAsync("explode", input, RetryPolicy.NoRetry);
        }
        catch (ActivityFailureException)
        {
        }

        return [.. charged, .. approval];
    });

    private static ActivityRegistry Activities() => new ActivityRegistry()
        .Register("charge", (_, i, _) => Task.FromResult(Encoding.UTF8.GetBytes("charged:" + Encoding.UTF8.GetString(i))))
        .Register("explode", (_, i, _) => throw new InvalidOperationException("card " + Encoding.UTF8.GetString(i) + " declined"));

    private static async Task<long> RowsContainingAsync(TestDatabase db, string plain)
    {
        string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(plain));
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(plain));
        var tables = new List<string>();
        await using (NpgsqlCommand list = db.Superuser.CreateCommand("SELECT tablename FROM pg_tables WHERE schemaname = 'kairos'"))
        await using (NpgsqlDataReader reader = await list.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        long hits = 0;
        foreach (string table in tables)
        {
            await using NpgsqlCommand cmd = db.Superuser.CreateCommand(
                $"SELECT count(*) FROM kairos.\"{table}\" x WHERE x::text ILIKE '%' || @plain || '%' OR x::text ILIKE '%' || @hex || '%' OR x::text LIKE '%' || @b64 || '%'");
            cmd.Parameters.AddWithValue("plain", plain);
            cmd.Parameters.AddWithValue("hex", hex);
            cmd.Parameters.AddWithValue("b64", b64);
            hits += (long)(await cmd.ExecuteScalarAsync())!;
        }

        return hits;
    }

    private static async Task<RunKey> RunAsync(TestDatabase db, IPayloadProtector? protector)
    {
        PostgresKairosStore store = db.NewStore();
        var time = new VirtualTimeProvider();
        var client = new KairosClient(store, time, payloadProtector: protector);
        var worker = new WorkerRuntime(store, Scope, Workflows(), Activities(), time, new WorkerOptions { AutoHeartbeat = false }, protector);

        StartRunResult start = await client.StartAsync(Scope, "pay-" + Guid.NewGuid(), "wf", Encoding.UTF8.GetBytes(Canary));
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, start.RunId);
        await Driver.DriveAsync(worker);
        await client.SignalAsync(run, "approve", Encoding.UTF8.GetBytes("+" + Canary));
        await Driver.DriveAsync(worker);
        Assert.Equal(RunState.Completed, await client.GetStateAsync(run));
        return run;
    }

    [Fact]
    public async Task With_encryption_no_table_holds_a_trace_of_any_payload()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        var protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());

        RunKey run = await RunAsync(db, protector);

        Assert.Equal(0, await RowsContainingAsync(db, Canary));
        Assert.Equal(0, await RowsContainingAsync(db, "charged:" + Canary));
        Assert.Equal(0, await RowsContainingAsync(db, "declined"));    // the error text is protected as well

        // The key holder still reads everything back, and the stored chain verifies.
        var client = new KairosClient(db.NewStore(), new VirtualTimeProvider(), payloadProtector: protector);
        IReadOnlyList<HistoryEvent> opened = await client.GetOpenedHistoryAsync(run);
        Assert.Equal("charged:" + Canary + "+" + Canary, Encoding.UTF8.GetString(((WorkflowCompleted)opened[^1]).Result));
    }

    [Fact]
    public async Task Control_without_encryption_the_same_scan_finds_the_payloads()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();

        await RunAsync(db, protector: null);

        Assert.True(await RowsContainingAsync(db, Canary) > 0, "The scan found nothing in an unencrypted run, so it proves nothing.");
        Assert.True(await RowsContainingAsync(db, "declined") > 0);
    }

    private sealed class CapturingLogger : ILoggerProvider, ILogger
    {
        public List<string> Messages { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception) + exception);
            }
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Embedded_mode_with_a_payload_protector_completes_and_logs_no_payload()
    {
        await using TestDatabase db = await fixture.NewDatabaseAsync();
        var logs = new CapturingLogger();
        var protector = new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral());

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddKairos(o =>
        {
            o.UsePostgres(db.DataPlaneConnectionString).UsePayloadProtector(protector);
            o.PollFallbackInterval = TimeSpan.FromMilliseconds(100);
            o.AddActivity("charge", (_, i, _) => Task.FromResult(Encoding.UTF8.GetBytes("charged:" + Encoding.UTF8.GetString(i))));
            o.AddWorkflow("wf", async (ctx, input) => await ctx.ExecuteActivityAsync("charge", input));
        });
        using IHost host = builder.Build();
        await host.StartAsync();

        var client = host.Services.GetRequiredService<KairosClient>();
        StartRunResult start = await client.StartAsync(KairosOptions.DefaultScope, "e2e", "wf", Encoding.UTF8.GetBytes(Canary));
        var run = new RunKey(KairosOptions.DefaultScope.TenantId, KairosOptions.DefaultScope.NamespaceId, start.RunId);
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (await client.GetStateAsync(run) != RunState.Completed && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Equal(RunState.Completed, await client.GetStateAsync(run));
        IReadOnlyList<HistoryEvent> opened = await client.GetOpenedHistoryAsync(run);
        Assert.Equal("charged:" + Canary, Encoding.UTF8.GetString(((WorkflowCompleted)opened[^1]).Result));
        await host.StopAsync();

        Assert.Equal(0, await RowsContainingAsync(db, Canary));
        Assert.DoesNotContain(logs.Messages, m => m.Contains(Canary, StringComparison.Ordinal));
    }
}
