using System.Security.Cryptography;

namespace Kairos.Testing.Simulation;

/// <summary>A blob store in memory that shows what it holds, so a test can search it for plaintext and tell referenced blobs from orphans.</summary>
public sealed class RecordingBlobStore : IBlobOffload
{
    private readonly Dictionary<(TenantScope Scope, string Hash), byte[]> _blobs = [];
    private readonly object _gate = new();

    public IReadOnlyList<(TenantScope Scope, string Hash, byte[] Content)> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _blobs.Select(b => (b.Key.Scope, b.Key.Hash, b.Value))];
            }
        }
    }

    public ValueTask<BlobRef> PutAsync(TenantScope scope, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var reference = new BlobRef(Convert.ToHexStringLower(SHA256.HashData(content.Span)), content.Length);
        lock (_gate)
        {
            _blobs[(scope, reference.Sha256Hex)] = content.ToArray();
        }

        return ValueTask.FromResult(reference);
    }

    public ValueTask<byte[]?> GetAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_blobs.TryGetValue((scope, reference.Sha256Hex), out byte[]? bytes))
            {
                return ValueTask.FromResult<byte[]?>(null);
            }

            return bytes.Length == reference.Length && Convert.ToHexStringLower(SHA256.HashData(bytes)) == reference.Sha256Hex
                ? ValueTask.FromResult<byte[]?>(bytes)
                : throw new InvalidDataException("the stored bytes no longer match the reference");
        }
    }

    public ValueTask<bool> DeleteAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_blobs.Remove((scope, reference.Sha256Hex)));
        }
    }
}

/// <summary>Puts, gets and deletes that fail the way a network does: before they happened, or after, with the caller never hearing back. They share the store's operation counter.</summary>
public sealed class FaultInjectingBlobOffload(IBlobOffload inner, FaultInjectingStore faults) : IBlobOffload
{
    public ValueTask<BlobRef> PutAsync(TenantScope scope, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        byte[] copy = content.ToArray();
        return faults.InterceptAsync(nameof(PutAsync), () => inner.PutAsync(scope, copy, cancellationToken));
    }

    public ValueTask<byte[]?> GetAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken) =>
        faults.InterceptAsync(nameof(GetAsync), () => inner.GetAsync(scope, reference, cancellationToken));

    public ValueTask<bool> DeleteAsync(TenantScope scope, BlobRef reference, CancellationToken cancellationToken) =>
        faults.InterceptAsync(nameof(DeleteAsync), () => inner.DeleteAsync(scope, reference, cancellationToken));
}
