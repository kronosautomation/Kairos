using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Kairos.Worker;

/// <summary>
/// Limits that decide how long a process may keep using a data key after the key provider stops allowing it, and how much one data key covers.
/// <para>
/// <b>What a data key opens.</b> Every payload sealed under one data key is opened by that key: whoever gets the unwrapped data key (a worker, a service that unwraps keys for a person's
/// browser) can open every value sealed with it, in that tenant and namespace, not only the one they asked about. A request that names a value (a log of intent) says what was wanted,
/// not what the key allows. So the unit of exposure is the data key, and it is bounded by <see cref="DataKeyLifetime"/> and <see cref="MaxUsesPerDataKey"/>: by default an hour or ten
/// thousand payloads, whichever comes first. A customer who wants a smaller unit shortens those, at the price of more wrap calls to the key provider
/// (see <see cref="OneKeyPerValue"/> and <see cref="ShortLived"/>).
/// </para>
/// </summary>
public sealed class EnvelopeOptions
{
    /// <summary>Payloads sealed under one data key before a new one is made (random 96-bit GCM nonces stay far below their safe bound). At least one.</summary>
    public int MaxUsesPerDataKey { get; init; } = 10_000;

    /// <summary>
    /// How long one data key stays in use for sealing, however quiet the worker. After this, the next payload needs a fresh wrap from the
    /// key provider, so a revoked or rotated master key stops being used for new payloads within this time. Must be positive. Default one hour.
    /// </summary>
    public TimeSpan DataKeyLifetime { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The tightest scope: every payload is sealed under a data key of its own, so a key that is unwrapped for one value opens that value and nothing else. It costs one wrap call to the
    /// key provider per payload (and one unwrap per value read), which is the price of the smaller blast radius; measure the provider's latency before choosing it for hot paths.
    /// </summary>
    public static EnvelopeOptions OneKeyPerValue { get; } = new() { MaxUsesPerDataKey = 1, DataKeyLifetime = TimeSpan.FromMinutes(1) };

    /// <summary>Data keys that are replaced after <paramref name="lifetime"/> or <paramref name="maxUses"/> payloads, whichever comes first.</summary>
    public static EnvelopeOptions ShortLived(TimeSpan lifetime, int maxUses = 100) => new() { DataKeyLifetime = lifetime, MaxUsesPerDataKey = maxUses };

    internal void Validate()
    {
        if (MaxUsesPerDataKey < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxUsesPerDataKey), "A data key seals at least one payload.");
        }

        if (DataKeyLifetime <= TimeSpan.Zero || UnwrappedKeyTtl <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DataKeyLifetime), "Key lifetimes must be positive.");
        }

        if (MaxCachedKeys < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxCachedKeys), "At least one unwrapped key must be cached.");
        }
    }

    /// <summary>
    /// How long an unwrapped data key stays cached for opening. After this the provider is asked again, so revoking the customer's key
    /// stops a running worker from opening anything not already open within this time (default 15 minutes).
    /// </summary>
    public TimeSpan UnwrappedKeyTtl { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Most unwrapped data keys cached at once.</summary>
    public int MaxCachedKeys { get; init; } = 256;
}

/// <summary>
/// Client-side envelope encryption. Payloads are sealed with AES-256-GCM under a data key; the
/// data key is wrapped by the customer's <see cref="IKeyProvider"/> (KMS, Key Vault, local key ring), so Kairos stores ciphertext only and
/// never sees a key. The authenticated data binds each payload to its tenant, namespace and caller-supplied context (run, field, id), so a
/// ciphertext cannot be replayed into another run, field or tenant.
/// <para>
/// Data keys are reused to avoid a provider round trip per payload, but only briefly (see <see cref="EnvelopeOptions"/>), and they are
/// wiped from memory when replaced or evicted (never while an operation is still using them).
/// </para>
/// </summary>
public sealed class EnvelopePayloadProtector : IPayloadProtector, IDisposable
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DataKeySize = 32;

    private readonly IKeyProvider _keys;
    private readonly TimeProvider _time;
    private readonly EnvelopeOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<TenantScope, ActiveKey> _active = [];
    private readonly Dictionary<string, KeyHolder> _unwrapped = new(StringComparer.Ordinal);

    public EnvelopePayloadProtector(IKeyProvider keys, TimeProvider? time = null, EnvelopeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
        _time = time ?? TimeProvider.System;
        _options = options ?? new EnvelopeOptions();
        _options.Validate();
    }

    public async ValueTask<byte[]> ProtectAsync(TenantScope scope, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        while (true)
        {
            ActiveKey active = await GetActiveKeyAsync(scope, cancellationToken).ConfigureAwait(false);
            if (!active.Holder.TryRent(out byte[]? dataKey))
            {
                continue; // retired between lookup and use; make a fresh one
            }

            try
            {
                return Seal(scope, active.Wrapped, dataKey, plaintext.Span, context.Span);
            }
            finally
            {
                active.Holder.Return();
            }
        }
    }

    public async ValueTask<byte[]> UnprotectAsync(TenantScope scope, ReadOnlyMemory<byte> protectedPayload, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        (WrappedKey wrapped, int headerLength) = ParseHeader(protectedPayload.Span);
        while (true)
        {
            KeyHolder holder = await GetDataKeyAsync(scope, wrapped, cancellationToken).ConfigureAwait(false);
            if (!holder.TryRent(out byte[]? dataKey))
            {
                continue;
            }

            try
            {
                return Decrypt(dataKey, protectedPayload.Span, headerLength, Aad(scope, context.Span));
            }
            finally
            {
                holder.Return();
            }
        }
    }

    /// <summary>Wipes every key held in memory (once nothing is using them).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (ActiveKey active in _active.Values)
            {
                active.Holder.Retire();
            }

            foreach (KeyHolder holder in _unwrapped.Values)
            {
                holder.Retire();
            }

            _active.Clear();
            _unwrapped.Clear();
        }
    }

    private static byte[] Seal(TenantScope scope, WrappedKey wrapped, byte[] dataKey, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> context)
    {
        byte[] keyId = Encoding.UTF8.GetBytes(wrapped.KeyId);
        int headerLength = 1 + 2 + keyId.Length + 2 + wrapped.Ciphertext.Length;
        byte[] output = new byte[headerLength + NonceSize + TagSize + plaintext.Length];
        output[0] = FormatVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(1), checked((ushort)keyId.Length));
        keyId.CopyTo(output, 3);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(3 + keyId.Length), checked((ushort)wrapped.Ciphertext.Length));
        wrapped.Ciphertext.CopyTo(output, 5 + keyId.Length);

        Span<byte> nonce = output.AsSpan(headerLength, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(dataKey, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(headerLength + NonceSize + TagSize), output.AsSpan(headerLength + NonceSize, TagSize), Aad(scope, context));
        return output;
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
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_active.TryGetValue(scope, out ActiveKey? current))
            {
                if (current.Uses < _options.MaxUsesPerDataKey && now < current.CreatedAt + _options.DataKeyLifetime)
                {
                    current.Uses++;
                    return current;
                }

                current.Holder.Retire(); // used up or too old: wiped as soon as nothing is mid-operation with it
                _active.Remove(scope);
            }
        }

        byte[] dataKey = RandomNumberGenerator.GetBytes(DataKeySize);
        WrappedKey wrapped;
        try
        {
            wrapped = await _keys.WrapAsync(dataKey, ScopeContext(scope), ct).ConfigureAwait(false);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw;
        }

        // Sealing and opening use separate copies: the sealing key lives for DataKeyLifetime, the opening copy only for UnwrappedKeyTtl.
        var fresh = new ActiveKey(new KeyHolder(dataKey, now + _options.DataKeyLifetime), wrapped, now) { Uses = 1 };
        var opening = new KeyHolder(dataKey.ToArray(), now + _options.UnwrappedKeyTtl);
        lock (_gate)
        {
            if (_active.Remove(scope, out ActiveKey? raced))
            {
                raced.Holder.Retire();
            }

            _active[scope] = fresh;
            Remember(CacheKey(scope, wrapped), opening);
        }

        return fresh;
    }

    private async ValueTask<KeyHolder> GetDataKeyAsync(TenantScope scope, WrappedKey wrapped, CancellationToken ct)
    {
        string cacheKey = CacheKey(scope, wrapped);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_unwrapped.TryGetValue(cacheKey, out KeyHolder? cached))
            {
                if (now < cached.ExpiresAt)
                {
                    return cached;
                }

                cached.Retire(); // past its TTL: forget it, so the provider is asked again (and can say no)
                _unwrapped.Remove(cacheKey);
            }
        }

        byte[] dataKey;
        try
        {
            dataKey = await _keys.UnwrapAsync(wrapped, ScopeContext(scope), ct).ConfigureAwait(false);
        }
        catch (KeyUnwrapException ex)
        {
            throw new PayloadProtectionException("The payload could not be opened.", ex);
        }

        var holder = new KeyHolder(dataKey, now + _options.UnwrappedKeyTtl);
        lock (_gate)
        {
            Remember(cacheKey, holder);
        }

        return holder;
    }

    private void Remember(string cacheKey, KeyHolder holder)
    {
        if (_unwrapped.Remove(cacheKey, out KeyHolder? replaced) && !ReferenceEquals(replaced, holder))
        {
            replaced.Retire();
        }

        if (_unwrapped.Count >= _options.MaxCachedKeys)
        {
            foreach (KeyHolder old in _unwrapped.Values)
            {
                old.Retire(); // bounded cache: dropping a key only costs a provider call later, and the wipe waits for any user
            }

            _unwrapped.Clear();
        }

        _unwrapped[cacheKey] = holder;
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

    private sealed class ActiveKey(KeyHolder holder, WrappedKey wrapped, DateTimeOffset createdAt)
    {
        public KeyHolder Holder { get; } = holder;

        public WrappedKey Wrapped { get; } = wrapped;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public int Uses { get; set; }
    }

    /// <summary>
    /// A data key that is zeroed when retired, but never while an operation still holds it: retiring only marks it; the wipe happens when
    /// the last user returns it (or immediately if nobody is using it).
    /// </summary>
    private sealed class KeyHolder(byte[] key, DateTimeOffset expiresAt)
    {
        private readonly object _lock = new();
        private int _users;
        private bool _retired;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public bool IsRetired
        {
            get
            {
                lock (_lock)
                {
                    return _retired;
                }
            }
        }

        public bool TryRent([NotNullWhen(true)] out byte[]? dataKey)
        {
            lock (_lock)
            {
                if (_retired)
                {
                    dataKey = null;
                    return false;
                }

                _users++;
                dataKey = key;
                return true;
            }
        }

        public void Return()
        {
            lock (_lock)
            {
                if (--_users == 0 && _retired)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }

        public void Retire()
        {
            lock (_lock)
            {
                _retired = true;
                if (_users == 0)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }
        }
    }
}
