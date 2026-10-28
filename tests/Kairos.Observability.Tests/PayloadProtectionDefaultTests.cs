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

        public void ReportPayloadProtection(bool payloadsProtected) => Reported.Add(payloadsProtected);
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
