namespace Kairos.Testing;

/// <summary>
/// Seeded xoshiro256** generator. Self-contained so a seed reproduces the same stream on every runtime and OS version
/// (unlike <see cref="Random"/>, whose algorithm is not a compatibility promise).
/// </summary>
public sealed class SimRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public SimRandom(ulong seed)
    {
        Seed = seed;
        ulong x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    public ulong Seed { get; }

    public ulong NextUInt64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Uniform in [0, <paramref name="exclusiveMax"/>).</summary>
    public int Next(int exclusiveMax)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusiveMax, 1);
        return (int)(NextUInt64() % (ulong)exclusiveMax);
    }

    /// <summary>Uniform in [min, max].</summary>
    public int Between(int min, int max) => min + Next(max - min + 1);

    public bool Chance(double probability) => (NextUInt64() >> 11) * (1.0 / (1UL << 53)) < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    public byte[] Bytes(int length)
    {
        byte[] b = new byte[length];
        for (int i = 0; i < length; i++)
        {
            b[i] = (byte)NextUInt64();
        }

        return b;
    }

    public Guid NextGuid()
    {
        Span<byte> b = stackalloc byte[16];
        BitConverter.TryWriteBytes(b, NextUInt64());
        BitConverter.TryWriteBytes(b[8..], NextUInt64());
        return new Guid(b);
    }

    /// <summary>An independent stream derived from this seed and a label, so adding draws elsewhere does not shift it.</summary>
    public SimRandom Fork(ulong salt) => new(Seed ^ (salt * 0x9E3779B97F4A7C15UL));

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    private static ulong SplitMix(ref ulong state)
    {
        ulong z = state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
