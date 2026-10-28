namespace Kairos;

/// <summary>
/// The reference a history keeps for an offloaded payload, and the escape that keeps it unambiguous. A payload's sealed bytes open to a <i>body</i>: either the payload itself or, for an offloaded payload,
/// a reference (16 bytes of magic, a version, the SHA-256 and the length of the sealed blob). A payload that happens to begin with either magic is stored with an escape prefix, so a body that begins
/// with the reference magic is a reference, always, and one that is not exactly a reference is damage, never data.
/// </summary>
internal static class BlobReferences
{
    private static readonly byte[] Magic = [0xF4, 0x25, 0xAB, 0xB5, 0x05, 0x96, 0x1A, 0xAE, 0xAA, 0x5B, 0x53, 0x5D, 0x22, 0x27, 0x34, 0xED];
    private static readonly byte[] EscapeMagic = [0x80, 0x0F, 0xDF, 0x74, 0x60, 0x33, 0x27, 0x12, 0x93, 0x09, 0x80, 0x59, 0xAC, 0xF6, 0xEC, 0xF0];
    private const byte Version = 1;
    private const int HashBytes = 32;
    internal const int EncodedLength = 16 + 1 + HashBytes + 8;

    /// <summary>The body to seal for a payload kept inline: the payload, with the escape in front if it could be mistaken for a reference.</summary>
    public static byte[] Escape(byte[] payload) =>
        StartsWith(payload, Magic) || StartsWith(payload, EscapeMagic) ? [.. EscapeMagic, .. payload] : payload;

    /// <summary>The payload, from a body that is not a reference.</summary>
    public static byte[] Unescape(byte[] body) => StartsWith(body, EscapeMagic) ? body[EscapeMagic.Length..] : body;

    public static byte[] Encode(BlobRef reference)
    {
        byte[] hash = Convert.FromHexString(reference.Sha256Hex);
        if (hash.Length != HashBytes)
        {
            throw new ArgumentException("A blob reference holds a SHA-256.", nameof(reference));
        }

        byte[] bytes = new byte[EncodedLength];
        Magic.CopyTo(bytes, 0);
        bytes[16] = Version;
        hash.CopyTo(bytes, 17);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(17 + HashBytes), reference.Length);
        return bytes;
    }

    /// <summary>True and the reference when the body is one; false when it is a payload. Throws when it begins like a reference and is not one (damage).</summary>
    public static bool TryDecode(byte[] body, out BlobRef reference)
    {
        reference = default;
        if (!StartsWith(body, Magic))
        {
            return false;
        }

        if (body.Length != EncodedLength || body[16] != Version)
        {
            throw new PayloadBlobException("A blob reference in the history is malformed or of a version this build does not know.");
        }

        long length = System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(17 + HashBytes));
        if (length < 0)
        {
            throw new PayloadBlobException("A blob reference in the history is malformed.");
        }

        reference = new BlobRef(Convert.ToHexStringLower(body.AsSpan(17, HashBytes)), length);
        return true;
    }

    private static bool StartsWith(byte[] value, byte[] prefix) => value.Length >= prefix.Length && value.AsSpan(0, prefix.Length).SequenceEqual(prefix);
}
