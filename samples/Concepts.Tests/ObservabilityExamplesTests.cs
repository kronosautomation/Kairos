using Kairos.Samples.Concepts;
using Microsoft.Extensions.DependencyInjection;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>The telemetry-setup region of the observability page: it registers Kairos and OpenTelemetry without naming the Kairos source or meter.</summary>
public sealed class ObservabilityExamplesTests
{
    private sealed class NoProtection : IPayloadProtector
    {
        public ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) => ValueTask.FromResult(plaintext.ToArray());

        public ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) => ValueTask.FromResult(protectedPayload.ToArray());
    }

    [Fact]
    public void The_sample_registers_Kairos_with_the_protector_it_was_given()
    {
        var services = new ServiceCollection();

        ObservabilityExamples.AddObservedKairos(services, "Host=localhost;Database=kairos;Username=kairos_dataplane", new NoProtection());

        Assert.Contains(services, d => d.ServiceType == typeof(KairosClient));
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<KairosOptions>().PayloadProtector);
    }
}
