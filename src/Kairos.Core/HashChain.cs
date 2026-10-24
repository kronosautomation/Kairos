using System.Buffers;
using System.Security.Cryptography;

namespace Kairos;

/// <summary>
/// Tamper-evident history: <c>Hash = SHA-256(PrevHash || canonical(event))</c>, anchored at 32 zero bytes.
/// </summary>
public static class HashChain
{
    public const int HashSize = 32;

    private static readonly byte[] s_genesis = new byte[HashSize];

    /// <summary>The <c>PrevHash</c> of the first event in every history.</summary>
    public static ReadOnlySpan<byte> GenesisHash => s_genesis;

    /// <summary>Returns a copy of <paramref name="e"/> chained to <paramref name="prevHash"/>, with its hash computed.</summary>
    public static HistoryEvent Seal(HistoryEvent e, ReadOnlySpan<byte> prevHash)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (prevHash.Length != HashSize)
        {
            throw new ArgumentException("Previous hash must be 32 bytes.", nameof(prevHash));
        }

        return e with { PrevHash = prevHash.ToArray(), Hash = ComputeHash(e, prevHash) };
    }

    /// <summary>Computes the hash of <paramref name="e"/> chained to <paramref name="prevHash"/> (ignores the event's own hash fields).</summary>
    public static byte[] ComputeHash(HistoryEvent e, ReadOnlySpan<byte> prevHash)
    {
        byte[] result = new byte[HashSize];
        ComputeHash(e, prevHash, result);
        return result;
    }

    /// <summary>Verifies sequence continuity, run identity, linkage and every recomputed hash. Throws on the first defect.</summary>
    /// <exception cref="HistoryIntegrityException">The history was tampered with, reordered, truncated at the front, or has a gap.</exception>
    public static void Verify(IReadOnlyList<HistoryEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count == 0)
        {
            return;
        }

        HistoryEvent first = history[0];
        byte[] expectedPrev = s_genesis;
        Span<byte> computed = stackalloc byte[HashSize];

        for (int i = 0; i < history.Count; i++)
        {
            HistoryEvent e = history[i];

            if (e.Sequence != i + 1)
            {
                throw new HistoryIntegrityException($"Expected sequence {i + 1} but found {e.Sequence}.", e.Sequence);
            }

            if (e.TenantId != first.TenantId || e.NamespaceId != first.NamespaceId || e.RunId != first.RunId)
            {
                throw new HistoryIntegrityException($"Event {e.Sequence} belongs to a different tenant, namespace, or run.", e.Sequence);
            }

            if (e.PrevHash.Length != HashSize || e.Hash.Length != HashSize ||
                !e.PrevHash.AsSpan().SequenceEqual(expectedPrev))
            {
                throw new HistoryIntegrityException($"Event {e.Sequence} is not linked to its predecessor.", e.Sequence);
            }

            ComputeHash(e, e.PrevHash, computed);
            if (!computed.SequenceEqual(e.Hash))
            {
                throw new HistoryIntegrityException($"Event {e.Sequence} content does not match its hash.", e.Sequence);
            }

            expectedPrev = e.Hash;
        }
    }

    private static void ComputeHash(HistoryEvent e, ReadOnlySpan<byte> prevHash, Span<byte> destination)
    {
        int max = HashSize + EventEncoder.MaxSize(e);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            prevHash.CopyTo(buffer);
            int length = HashSize + EventEncoder.Write(e, buffer.AsSpan(HashSize));
            SHA256.HashData(buffer.AsSpan(0, length), destination);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
