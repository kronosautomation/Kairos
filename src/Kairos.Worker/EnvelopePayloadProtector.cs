using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kairos.Worker;

/// <summary>
/// Client-side envelope encryption (docs/SECURITY-ARCHITECTURE.md section 4). Payloads are sealed with AES-256-GCM under a data key; the
/// data key is wrapped by the customer's <see cref="IKeyProvider"/> (KMS, Key Vault, local key ring), so Kairos stores ciphertext only and
/// never sees a key. The authenticated data binds each payload to its tenant, namespace and caller-supplied context (run, field, id), so a
/// ciphertext cannot be replayed into another run, field or tenant.
/// <para>
/// To avoid a key-provider round trip per payload, one data key per scope is reused for up to <see cref="MaxUsesPerDataKey"/> payloads
/// (random 96-bit nonces stay far below the GCM safety bound at that count), and unwrapped keys are kept in a small bounded cache.
/// </para>
/// </summary>
public sealed class EnvelopePayloadProtector : IPayloadProtector
{
    public const int MaxUsesPerDataKey = 10_000;
    public const int MaxCachedUnwrappedKeys = 256;

    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DataKeySize = 32;

    private readonly IKeyProvider _keys;
    private readonly object _gate = new();
    private readonly Dictionary<TenantScope, ActiveKey> _active = [];
    private readonly Dictionary<string, byte[]> _unwrapped = new(StringComparer.Ordinal);

    public EnvelopePayloadProtector(IKeyProvider keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    public async ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        ActiveKey active = await GetActiveKeyAsync(scope, cancellationToken).ConfigureAwait(false);
        byte[] keyId = Encoding.UTF8.GetBytes(active.Wrapped.KeyId);
        byte[] wrapped = active.Wrapped.Ciphertext;

        int headerLength = 1 + 2 + keyId.Length + 2 + wrapped.Length;
        byte[] output = new byte[headerLength + NonceSize + TagSize + plaintext.Length];
        output[0] = FormatVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(1), checked((ushort)keyId.Length));
        keyId.CopyTo(output, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(3 + keyId.Length), checked((ushort)wrapped.Length));
        wrapped.CopyTo(output, 5 + keyId.Length);

        Span<byte> nonce = output.AsSpan(headerLength, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(active.DataKey, TagSize);
        aes.Encrypt(nonce, plaintext.Span, output.AsSpan(headerLength + NonceSize + TagSize), output.AsSpan(headerLength + NonceSize, TagSize), Aad(scope, context.Span));
        return output;
    }

    public async ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        (WrappedKey wrapped, int headerLength) = ParseHeader(protectedPayload.Span);
        byte[] dataKey = await GetDataKeyAsync(scope, wrapped, cancellationToken).ConfigureAwait(false);
        return Decrypt(dataKey, protectedPayload.Span, headerLength, Aad(scope, context.Span));
    }

    private static (WrappedKey Wrapped, int HeaderLength) ParseHeader(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 1 + 2 + 2 + NonceSize + TagSize || blob[0] != FormatVersion)
        {
            throw new PayloadProtectionException();
        }

        int keyIdLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[1..]);
        if (blob.Length < 3 + keyIdLength + 2)
        {
            throw new PayloadProtectionException();
        }

        int wrappedLength = BinaryPrimitives.ReadUInt16LittleEndian(blob[(3 + keyIdLength)..]);
        int headerLength = 5 + keyIdLength + wrappedLength;
        if (blob.Length < headerLength + NonceSize + TagSize)
        {
            throw new PayloadProtectionException();
        }

        try
        {
            string keyId = Encoding.UTF8.GetString(blob.Slice(3, keyIdLength));
            return (new WrappedKey(keyId, blob.Slice(5 + keyIdLength, wrappedLength).ToArray()), headerLength);
        }
        catch (ArgumentException ex)
        {
            throw new PayloadProtectionException("The payload could not be opened.", ex);
        }
    }

    private static byte[] Decrypt(byte[] dataKey, ReadOnlySpan<byte> blob, int headerLength, byte[] aad)
    {
        byte[] plaintext = new byte[blob.Length - headerLength - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(dataKey, TagSize);
            aes.Decrypt(
                blob.Slice(headerLength, NonceSize), blob[(headerLength + NonceSize + TagSize)..], blob.Slice(headerLength + NonceSize, TagSize),
                plaintext, aad);
        }
        catch (CryptographicException ex)
        {
            throw new PayloadProtectionException("The payload could not be opened.", ex); // wrong context, wrong tenant, or altered: indistinguishable
        }

        return plaintext;
    }

    private async ValueTask<ActiveKey> GetActiveKeyAsync(TenantScope scope, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(scope, out ActiveKey? current) && current.Uses < MaxUsesPerDataKey)
            {
                current.Uses++;
                return current;
            }
        }

        byte[] dataKey = RandomNumberGenerator.GetBytes(DataKeySize);
        WrappedKey wrapped = await _keys.WrapAsync(dataKey, ScopeContext(scope), ct).ConfigureAwait(false);
        var fresh = new ActiveKey(dataKey, wrapped) { Uses = 1 };
        lock (_gate)
        {
            _active[scope] = fresh;
            Remember(scope, wrapped, dataKey);
        }

        return fresh;
    }

    private async ValueTask<byte[]> GetDataKeyAsync(TenantScope scope, WrappedKey wrapped, CancellationToken ct)
    {
        string cacheKey = CacheKey(scope, wrapped);
        lock (_gate)
        {
            if (_unwrapped.TryGetValue(cacheKey, out byte[]? cached))
            {
                return cached;
            }
        }

        try
        {
            byte[] dataKey = await _keys.UnwrapAsync(wrapped, ScopeContext(scope), ct).ConfigureAwait(false);
            lock (_gate)
            {
                Remember(scope, wrapped, dataKey);
            }

            return dataKey;
        }
        catch (KeyUnwrapException ex)
        {
            throw new PayloadProtectionException("The payload could not be opened.", ex);
        }
    }

    private void Remember(TenantScope scope, WrappedKey wrapped, byte[] dataKey)
    {
        if (_unwrapped.Count >= MaxCachedUnwrappedKeys)
        {
            _unwrapped.Clear(); // bounded: re-unwrapping is only a provider call
        }

        _unwrapped[CacheKey(scope, wrapped)] = dataKey;
    }

    private static string CacheKey(TenantScope scope, WrappedKey wrapped) =>
        $"{scope.TenantId:N}{scope.NamespaceId:N}{wrapped.KeyId}:{Convert.ToBase64String(wrapped.Ciphertext)}";

    private static byte[] ScopeContext(TenantScope scope)
    {
        byte[] bytes = new byte[32];
        scope.TenantId.TryWriteBytes(bytes, bigEndian: true, out _);
        scope.NamespaceId.TryWriteBytes(bytes.AsSpan(16), bigEndian: true, out _);
        return bytes;
    }

    private static byte[] Aad(TenantScope scope, ReadOnlySpan<byte> context)
    {
        byte[] aad = new byte[32 + context.Length];
        ScopeContext(scope).CopyTo(aad, 0);
        context.CopyTo(aad.AsSpan(32));
        return aad;
    }

    private sealed class ActiveKey(byte[] dataKey, WrappedKey wrapped)
    {
        public byte[] DataKey { get; } = dataKey;

        public WrappedKey Wrapped { get; } = wrapped;

        public int Uses { get; set; }
    }
}
