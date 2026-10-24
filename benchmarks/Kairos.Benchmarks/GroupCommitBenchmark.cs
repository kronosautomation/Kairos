using System.Diagnostics;
using Kairos;
using Kairos.Persistence.Postgres;
using Npgsql;
using Testcontainers.PostgreSql;

/// <summary>
/// R3 (docs/RESEARCH.md): does batching several workflow-task completions into one Postgres transaction pay off?
/// Closed-loop load: N workers each claim a task and complete it (boundary + one scheduled activity = 3 history rows, a run
/// update, a task delete, an activity-task insert). Reports completions/s and the p50/p99 latency of the completion call for
/// single commits versus group commit. Uses KAIROS_BENCH_PG (admin connection string) or starts a Postgres container.
/// Run: dotnet run -c Release --project benchmarks/Kairos.Benchmarks -- groupcommit
/// </summary>
internal static class GroupCommitBenchmark
{
    private const int TasksPerConfig = 3000;

    public static async Task<int> RunAsync()
    {
        PostgreSqlContainer? container = null;
        string adminCs;
        if (Environment.GetEnvironmentVariable("KAIROS_BENCH_PG") is { Length: > 0 } fromEnv)
        {
            adminCs = fromEnv;
        }
        else
        {
            container = new PostgreSqlBuilder(Environment.GetEnvironmentVariable("KAIROS_PG_IMAGE") ?? "postgres:17").Build();
            await container.StartAsync();
            adminCs = container.GetConnectionString();
        }

        try
        {
            await using NpgsqlDataSource admin = NpgsqlDataSource.Create(adminCs);
            await PostgresProvisioning.CreateRolesAsync(admin, "m", "d", "c", "r");
            await PostgresProvisioning.PrepareDatabaseAsync(admin);
            var migratorCs = new NpgsqlConnectionStringBuilder(adminCs) { Username = PostgresProvisioning.MigratorRole, Password = "m" };
            await using (NpgsqlDataSource migrator = NpgsqlDataSource.Create(migratorCs.ConnectionString))
            {
                await PostgresProvisioning.MigrateAsync(migrator);
            }

            var dataCs = new NpgsqlConnectionStringBuilder(adminCs) { Username = PostgresProvisioning.DataPlaneRole, Password = "d", MaxPoolSize = 200 };
            await using NpgsqlDataSource data = NpgsqlDataSource.Create(dataCs.ConnectionString);
            var store = new PostgresKairosStore(data);

            Console.WriteLine($"Postgres: {await ServerVersion(admin)}; {TasksPerConfig} task completions per configuration; {Environment.ProcessorCount} client cores");
            Console.WriteLine();
            Console.WriteLine("| mode | workers | completions/s | avg batch | p50 ms | p99 ms |");
            Console.WriteLine("|---|---:|---:|---:|---:|---:|");

            // Warm up pools, plans and the JIT once.
            await MeasureAsync(store, workers: 4, maxBatch: 0, linger: TimeSpan.Zero, tasks: 300, report: false);

            foreach (int workers in new[] { 4, 16, 64 })
            {
                await MeasureAsync(store, workers, maxBatch: 0, TimeSpan.Zero, TasksPerConfig, report: true);
                await MeasureAsync(store, workers, maxBatch: 8, TimeSpan.FromMilliseconds(1), TasksPerConfig, report: true);
                await MeasureAsync(store, workers, maxBatch: 32, TimeSpan.FromMilliseconds(2), TasksPerConfig, report: true);
            }

            return 0;
        }
        finally
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }
    }

    private static async Task<string> ServerVersion(NpgsqlDataSource admin)
    {
        await using NpgsqlCommand cmd = admin.CreateCommand("SHOW server_version");
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task MeasureAsync(PostgresKairosStore store, int workers, int maxBatch, TimeSpan linger, int tasks, bool report)
    {
        var scope = new TenantScope(Guid.NewGuid(), Guid.NewGuid());
        var client = new KairosClient(store, TimeProvider.System);
        await Parallel.ForEachAsync(Enumerable.Range(0, tasks), new ParallelOptions { MaxDegreeOfParallelism = 32 },
            async (i, ct) => await client.StartAsync(scope, $"gc-{i}", "wf", [1], cancellationToken: ct));

        await using WorkflowTaskGroupCommitter? committer = maxBatch > 0 ? new WorkflowTaskGroupCommitter(store, maxBatch, linger, writers: 4) : null;
        var latencies = new List<double>(tasks);
        long remaining = tasks;
        var watch = Stopwatch.StartNew();

        Task[] loops = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
        {
            var mine = new List<double>();
            while (Interlocked.Decrement(ref remaining) >= 0)
            {
                WorkflowTask? task = null;
                while (task is null)
                {
                    task = await store.PollWorkflowTaskAsync(scope, [KairosClient.DefaultBuildId], DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), CancellationToken.None);
                }

                RunKey r = task.Run;
                HistoryEvent[] events = [new ActivityScheduled(r.TenantId, r.NamespaceId, r.RunId, 0, DateTimeOffset.UtcNow, 1, "act", [1], RetryPolicy.Default)];
                long t0 = Stopwatch.GetTimestamp();
                CompletionOutcome outcome = committer is null
                    ? await store.CompleteWorkflowTaskAsync(task, events, DateTimeOffset.UtcNow, CancellationToken.None)
                    : await committer.SubmitAsync(task, events, DateTimeOffset.UtcNow);
                mine.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                if (outcome != CompletionOutcome.Applied)
                {
                    throw new InvalidOperationException($"Unexpected outcome {outcome}");
                }
            }

            lock (latencies)
            {
                latencies.AddRange(mine);
            }
        })).ToArray();
        await Task.WhenAll(loops);
        watch.Stop();

        if (!report)
        {
            return;
        }

        latencies.Sort();
        double Pct(double p) => latencies[Math.Min(latencies.Count - 1, (int)(latencies.Count * p))];
        double avgBatch = committer is null || committer.Batches == 0 ? 1 : (double)committer.Completions / committer.Batches;
        string mode = maxBatch == 0 ? "single commit" : $"group {maxBatch} / {linger.TotalMilliseconds:0}ms";
        Console.WriteLine($"| {mode} | {workers} | {latencies.Count / watch.Elapsed.TotalSeconds:N0} | {avgBatch:0.0} | {Pct(0.5):0.00} | {Pct(0.99):0.00} |");
    }
}
