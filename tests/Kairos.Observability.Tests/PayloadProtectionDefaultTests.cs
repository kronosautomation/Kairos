using Kairos.Agents;
using Kairos.Cloud.SelfHosted;
using Kairos.Testing;
using Kairos.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kairos.Observability.Tests;

/// <summary>Payloads are encrypted by default: a host with no protector refuses to be built unless the code says, in so many words, that readable payloads are intended; and that choice is logged.</summary>
public sealed class PayloadProtectionDefaultTests
{
    private sealed class Lines : ILoggerProvider
    {
        public List<(LogLevel Level, string Text)> All { get; } = [];

        /// <summary>The workers' start-up lines are written from the service's own thread of execution, so the tests wait for the line they expect (and, for a line that must not appear, give it a moment).</summary>
        public async Task WaitForAsync(Func<(LogLevel Level, string Text), bool> predicate, TimeSpan? limit = null)
        {
            DateTime deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(5));
            while (DateTime.UtcNow < deadline)
            {
                lock (All)
                {
                    if (All.Any(predicate))
                    {
                        return;
                    }
                }

                await Task.Delay(10);
            }
        }

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose()
        {
        }

        private sealed class Sink(Lines owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.All)
                {
                    owner.All.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }

    private static IServiceCollection Add(IServiceCollection services, Action<KairosOptions> configure) =>
        services.AddKairos(o =>
        {
            o.UseStore(new InMemoryKairosStore());
            configure(o);
        });

    private sealed class ReportingStore(IKairosStore inner) : ForwardingStore(inner), IPayloadPostureReporting
    {
        public List<bool> Reported { get; } = [];

        public int UnverifiedReports { get; private set; }

        public void ReportPayloadProtection(bool payloadsProtected) => Reported.Add(payloadsProtected);

        public void ReportUnverifiedApprovers() => UnverifiedReports++;
    }

    private static async Task<(List<(LogLevel Level, string Text)> Log, ReportingStore Store)> StartWithAgentsAsync(Action<AgentsBuilder> configure)
    {
        var lines = new Lines();
        var store = new ReportingStore(new InMemoryKairosStore());
        var agentsBuilder = new AgentsBuilder();
        configure(agentsBuilder);
        AgentsRuntime agents = agentsBuilder.Build();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(lines);
        _ = builder.Services.AddKairos(o => o.UseStore(store).AllowUnencryptedPayloads().AddActivities(agents.Activities));
        using IHost host = builder.Build();
        _ = host.Services.GetRequiredService<IKairosStore>();
        await host.StartAsync();
        await lines.WaitForAsync(l => l.Level == LogLevel.Warning && l.Text.Contains("approver", StringComparison.OrdinalIgnoreCase), TimeSpan.FromMilliseconds(300));
        await host.StopAsync();
        lock (lines.All)
        {
            return ([.. lines.All], store);
        }
    }

    [Fact]
    public async Task A_host_whose_agents_have_no_approver_verifier_is_warned_at_start_that_decisions_will_be_refused()
    {
        (List<(LogLevel Level, string Text)> log, ReportingStore store) = await StartWithAgentsAsync(_ => { });

        Assert.Contains(log, l => l.Level == LogLevel.Warning && l.Text.Contains("every approval decision will be refused", StringComparison.Ordinal));
        Assert.Equal(0, store.UnverifiedReports);
    }

    [Fact]
    public async Task Allowing_unverified_approvers_is_warned_at_start_and_reported_to_the_store()
    {
        (List<(LogLevel Level, string Text)> log, ReportingStore store) = await StartWithAgentsAsync(b => b.AllowUnverifiedApprovers());

        Assert.Contains(log, l => l.Level == LogLevel.Warning && l.Text.Contains("verification is OFF", StringComparison.Ordinal));
        Assert.Equal(1, store.UnverifiedReports);
    }

    [Fact]
    public async Task A_verifier_is_not_warned_about_and_nothing_is_reported()
    {
        (List<(LogLevel Level, string Text)> log, ReportingStore store) = await StartWithAgentsAsync(b => b.UseApproverVerifier(new AnyoneVerifier()));

        Assert.DoesNotContain(log, l => l.Text.Contains("approver", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, store.UnverifiedReports);
    }

    private sealed class AnyoneVerifier : IApproverVerifier
    {
        public ValueTask<VerifiedApprover?> VerifyAsync(ApproverEvidenceContext context, CancellationToken cancellationToken) => ValueTask.FromResult<VerifiedApprover?>(null);
    }

    [Fact]
    public void A_store_that_talks_to_a_server_is_told_whether_this_process_encrypts_payloads()
    {
        var readable = new ReportingStore(new InMemoryKairosStore());
        var protectedStore = new ReportingStore(new InMemoryKairosStore());

        using (ServiceProvider provider = new ServiceCollection().AddKairos(o => o.UseStore(readable).AllowUnencryptedPayloads()).BuildServiceProvider())
        {
            _ = provider.GetRequiredService<KairosClient>();
            _ = provider.GetRequiredService<IKairosStore>();
        }

        using (ServiceProvider provider = new ServiceCollection().AddKairos(o => o.UseStore(protectedStore).UsePayloadProtector(new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral()))).BuildServiceProvider())
        {
            _ = provider.GetRequiredService<IKairosStore>();
        }

        Assert.Equal([false], readable.Reported);
        Assert.Equal([true], protectedStore.Reported);
    }

    [Fact]
    public void A_host_with_no_protector_and_no_explicit_opt_out_refuses_to_start()
    {
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => Add(new ServiceCollection(), _ => { }));

        Assert.Contains("UsePayloadProtector", e.Message, StringComparison.Ordinal);
        Assert.Contains("AllowUnencryptedPayloads", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_protector_is_enough()
    {
        var services = new ServiceCollection();

        _ = Add(services, o => o.UsePayloadProtector(new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral())));

        Assert.NotEmpty(services);
    }

    [Fact]
    public async Task Choosing_readable_payloads_is_allowed_and_logged_as_a_warning_when_the_workers_start()
    {
        var lines = new Lines();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(lines);
        _ = Add(builder.Services, o => o.AllowUnencryptedPayloads());

        using IHost host = builder.Build();
        await host.StartAsync();
        await lines.WaitForAsync(l => l.Text.Contains("encryption is OFF", StringComparison.Ordinal));
        await host.StopAsync();

        lock (lines.All)
        {
            Assert.Contains(lines.All, l => l.Level == LogLevel.Warning && l.Text.Contains("encryption is OFF", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_protected_host_logs_no_such_warning()
    {
        var lines = new Lines();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(lines);
        _ = Add(builder.Services, o => o.UsePayloadProtector(new EnvelopePayloadProtector(LocalKeyProvider.CreateEphemeral())));

        using IHost host = builder.Build();
        await host.StartAsync();
        await host.StopAsync();

        lock (lines.All)
        {
            Assert.DoesNotContain(lines.All, l => l.Text.Contains("encryption is OFF", StringComparison.Ordinal));
        }
    }
}
