using Kairos.Agents;
using Kairos.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Observability.Tests;

/// <summary>An adapter can add a start-up diagnostic to the agents builder; it is said in the host's log when the workers start, and one that fails does not stop the host.</summary>
public sealed class StartupDiagnosticsTests
{
    private sealed class Lines : ILoggerProvider, ILogger
    {
        public List<(LogLevel Level, string Text)> All { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public void Dispose()
        {
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (All)
            {
                All.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static async Task<List<(LogLevel Level, string Text)>> StartAsync(Action<AgentsBuilder> configure)
    {
        var lines = new Lines();
        var builder = new AgentsBuilder().AllowUnverifiedApprovers();
        configure(builder);
        AgentsRuntime agents = builder.Build();
        HostApplicationBuilder host = Host.CreateApplicationBuilder();
        host.Logging.ClearProviders();
        host.Logging.AddProvider(lines);
        _ = host.Services.AddKairos(o => o.UseStore(new InMemoryKairosStore()).AllowUnencryptedPayloads().AddActivities(agents.Activities));
        using IHost app = host.Build();
        await app.StartAsync();
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (lines.All)
            {
                if (lines.All.Any(l => l.Text.Contains("Approver verification is OFF", StringComparison.Ordinal)))
                {
                    break;
                }
            }

            await Task.Delay(10);
        }

        await Task.Delay(100); // the diagnostics follow the warnings on the same thread
        await app.StopAsync();
        lock (lines.All)
        {
            return [.. lines.All];
        }
    }

    [Fact]
    public async Task A_startup_hook_is_said_in_the_host_log_as_information_and_warning()
    {
        List<(LogLevel Level, string Text)> log = await StartAsync(b => b.OnStartup(r =>
        {
            r.Information("diagnostic one");
            r.Warning("diagnostic two");
        }));

        Assert.Contains(log, l => l is { Level: LogLevel.Information, Text: "diagnostic one" });
        Assert.Contains(log, l => l is { Level: LogLevel.Warning, Text: "diagnostic two" });
    }

    [Fact]
    public async Task A_hook_that_throws_is_reported_and_the_others_still_run_and_the_host_started()
    {
        List<(LogLevel Level, string Text)> log = await StartAsync(b => b
            .OnStartup(_ => throw new InvalidOperationException("secret detail"))
            .OnStartup(r => r.Information("after")));

        Assert.Contains(log, l => l.Level == LogLevel.Warning && l.Text.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.DoesNotContain(log, l => l.Text.Contains("secret detail", StringComparison.Ordinal)); // the message may hold anything; only the type is said
        Assert.Contains(log, l => l is { Level: LogLevel.Information, Text: "after" });
    }

    [Fact]
    public async Task Without_a_hook_nothing_is_added()
    {
        List<(LogLevel Level, string Text)> log = await StartAsync(_ => { });
        Assert.DoesNotContain(log, l => l.Text.Contains("diagnostic", StringComparison.Ordinal));
    }
}
