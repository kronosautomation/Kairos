using System.Security.Cryptography;
using System.Text;

namespace Kairos.Cloud.SelfHosted;

/// <summary>
/// A key ring held by the application (keys come from a secret store, file or environment; never from the Kairos server). Each master
/// key wraps data keys with AES-256-GCM, authenticating the key id and the caller's context. Rotate by adding a key and making it
/// current; older keys keep unwrapping what they sealed.
/// </summary>
public sealed class LocalKeyProvider : IKeyProvider
{
    public const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _currentKeyId;

    public LocalKeyProvider(IReadOnlyDictionary<string, byte[]> keys, string currentKeyId)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrEmpty(currentKeyId);
        if (!keys.ContainsKey(currentKeyId))
        {
            throw new ArgumentException("The current key id is not in the key ring.", nameof(currentKeyId));
        }

        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach ((string id, byte[] key) in keys)
        {
            if (key.Length != KeySize)
            {
                throw new ArgumentException($"Key '{id}' must be {KeySize} bytes (AES-256).", nameof(keys));
            }

            _keys.Add(id, [.. key]);
        }

        _currentKeyId = currentKeyId;
    }

    /// <summary>A provider with one fresh random key. For development and tests only: the key is lost with the process.</summary>
    public static LocalKeyProvider CreateEphemeral(string keyId = "dev-1") =>
        new(new Dictionary<string, byte[]> { [keyId] = RandomNumberGenerator.GetBytes(KeySize) }, keyId);

    public ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] output = new byte[NonceSize + TagSize + dataKey.Length];
        Span<byte> nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_keys[_currentKeyId], TagSize);
        aes.Encrypt(nonce, dataKey.Span, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), Aad(_currentKeyId, context.Span));
        return ValueTask.FromResult(new WrappedKey(_currentKeyId, output));
    }

    public ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (wrapped.KeyId is null || wrapped.Ciphertext is null || !_keys.TryGetValue(wrapped.KeyId, out byte[]? master) ||
            wrapped.Ciphertext.Length < NonceSize + TagSize)
        {
            throw new KeyUnwrapException();
        }

        ReadOnlySpan<byte> blob = wrapped.Ciphertext;
        byte[] plain = new byte[blob.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(master, TagSize);
            aes.Decrypt(blob[..NonceSize], blob[(NonceSize + TagSize)..], blob.Slice(NonceSize, TagSize), plain, Aad(wrapped.KeyId, context.Span));
        }
        catch (CryptographicException ex)
        {
            throw new KeyUnwrapException("The data key could not be unwrapped.", ex); // never says which part was wrong
        }

        return ValueTask.FromResult(plain);
    }

    private static byte[] Aad(string keyId, ReadOnlySpan<byte> context)
    {
        byte[] id = Encoding.UTF8.GetBytes(keyId);
        byte[] aad = new byte[4 + id.Length + context.Length];
        BitConverter.TryWriteBytes(aad, id.Length);
        id.CopyTo(aad, 4);
        context.CopyTo(aad.AsSpan(4 + id.Length));
        return aad;
    }
}
