using Kairos.Samples.Concepts;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>The blob-offload region of the payload-encryption page configures what it says: the protector and the store, with the threshold given.</summary>
public sealed class BlobOffloadExamplesTests
{
    private sealed class NoProtection : IPayloadProtector
    {
        public ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) => ValueTask.FromResult(plaintext.ToArray());

        public ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) => ValueTask.FromResult(protectedPayload.ToArray());
    }

    private sealed class NoBlobs : IBlobOffload
    {
        public ValueTask<BlobRef> PutAsync(TenantScope scope, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<byte[]?> GetAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<bool> DeleteAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void The_example_sets_the_protector_the_store_and_the_threshold()
    {
        var protector = new NoProtection();
        var blobs = new NoBlobs();
        var options = new KairosOptions();

        BlobOffloadExamples.Offload(options, protector, blobs);

        Assert.Same(protector, options.PayloadProtector);
        Assert.Same(blobs, options.BlobOffload!.Store);
        Assert.Equal(128 * 1024, options.BlobOffload.ThresholdBytes);
    }
}
