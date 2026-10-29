using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Kairos.Cloud.SelfHosted;

/// <summary>Why a call to Vault failed. Never carries a token, a key or a response body.</summary>
public enum VaultFailure
{
    /// <summary>Vault refused the token (401 or 403), also after the token was read again.</summary>
    Authentication,

    /// <summary>The mount or the key does not exist (404).</summary>
    NotFound,

    /// <summary>Vault could not be reached, timed out, was sealed or overloaded (5xx, 429, network): try again.</summary>
    Unavailable,

    /// <summary>Vault refused the request (another 4xx), or its answer could not be understood or was too large.</summary>
    Rejected,

    /// <summary>The token could not be obtained (file missing, empty or malformed; login refused).</summary>
    Token,
}

/// <summary>A call to Vault failed for an operational reason. The message never contains the token.</summary>
public sealed class VaultTransitException : Exception
{
    public VaultTransitException(VaultFailure failure, int statusCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    public VaultFailure Failure { get; }

    /// <summary>The HTTP status Vault answered with, or 0 when there was none.</summary>
    public int StatusCode { get; }
}

/// <summary>
/// Wraps data keys with a HashiCorp Vault or OpenBao Transit key. The Kairos context goes in Transit's <c>context</c> parameter of a
/// derived key, so Vault itself refuses to unwrap a key for another context, as a cloud KMS does with its encryption context.
/// The provider only ever calls <c>transit/keys/&lt;key&gt;</c> (once, to check the key is derived), <c>transit/encrypt/&lt;key&gt;</c> and
/// <c>transit/decrypt/&lt;key&gt;</c>, on the one mount and key it is configured with; stored key ids are compared with that name and
/// never used to build a request. <see cref="WrappedKey.KeyId"/> is <c>mount/key#version</c>.
/// </summary>
public sealed partial class VaultTransitKeyProvider : IKeyProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly VaultTransitOptions _options;
    private readonly string _mount;
    private readonly string _key;
    private readonly string _pinnedId;
    private readonly string _namespace;
    private readonly TokenSource _tokens;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private volatile bool _keyChecked;
    private volatile string? _checkedToken;

    /// <summary>Creates the provider. Pass <paramref name="httpClient"/> only to supply your own handler (proxy, client certificate); it must not follow redirects.</summary>
    public VaultTransitKeyProvider(VaultTransitOptions options, HttpClient? httpClient = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _mount = options.Mount;
        _key = options.KeyName!;
        _pinnedId = _mount + "/" + _key;
        _namespace = options.Namespace ?? string.Empty;
        if (httpClient is null)
        {
            _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
        }

        _tokens = new TokenSource(options.Auth!, this, timeProvider ?? TimeProvider.System);
    }

    public async ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dataKey.IsEmpty)
        {
            throw new ArgumentException("The data key is empty.", nameof(dataKey));
        }

        if (context.IsEmpty)
        {
            throw new ArgumentException("A context is required: a Transit derived key cannot bind to nothing.", nameof(context));
        }

        await EnsureKeyCheckedAsync(cancellationToken).ConfigureAwait(false);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(
            new EncryptRequest(Convert.ToBase64String(dataKey.Span), Convert.ToBase64String(context.Span)), VaultJson.Default.EncryptRequest);
        (int status, byte[] response) = await SendAuthorizedAsync(HttpMethod.Post, $"v1/{_mount}/encrypt/{_key}", body, cancellationToken).ConfigureAwait(false);
        if (status != 200)
        {
            throw Failure(status, "wrap");
        }

        EncryptResponse? parsed = Parse(response, VaultJson.Default.EncryptResponse);
        string? ciphertext = parsed?.Data?.Ciphertext;
        if (ciphertext is null || !TryParseCiphertext(ciphertext, out int version))
        {
            throw new VaultTransitException(VaultFailure.Rejected, status, "Vault Transit returned a wrapped key in a form that was not expected.");
        }

        return new WrappedKey(_pinnedId + "#" + version.ToString(CultureInfo.InvariantCulture), Encoding.ASCII.GetBytes(ciphertext));
    }

    public async ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Everything that can be checked without Vault is checked first, and every answer is the same one.
        if (context.IsEmpty || wrapped.KeyId is null || wrapped.Ciphertext is null || wrapped.Ciphertext.Length is 0 or > 16 * 1024 ||
            !TryParseKeyId(wrapped.KeyId, out int idVersion))
        {
            throw new KeyUnwrapException();
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(wrapped.Ciphertext);
        }
        catch (ArgumentException)
        {
            throw new KeyUnwrapException();
        }

        if (!TryParseCiphertext(text, out int cipherVersion) || (idVersion != 0 && idVersion != cipherVersion))
        {
            throw new KeyUnwrapException();
        }

        await EnsureKeyCheckedAsync(cancellationToken).ConfigureAwait(false);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new DecryptRequest(text, Convert.ToBase64String(context.Span)), VaultJson.Default.DecryptRequest);
        (int status, byte[] response) = await SendAuthorizedAsync(HttpMethod.Post, $"v1/{_mount}/decrypt/{_key}", body, cancellationToken).ConfigureAwait(false);
        if (status is 400 or 404)
        {
            throw new KeyUnwrapException(); // wrong context, altered ciphertext, a deleted key or a version that may no longer decrypt: one answer
        }

        if (status != 200)
        {
            throw Failure(status, "unwrap");
        }

        DecryptResponse? parsed = Parse(response, VaultJson.Default.DecryptResponse);
        if (parsed?.Data?.Plaintext is not { } plain)
        {
            throw new VaultTransitException(VaultFailure.Rejected, status, "Vault Transit returned an unwrapped key in a form that was not expected.");
        }

        try
        {
            return Convert.FromBase64String(plain);
        }
        catch (FormatException)
        {
            throw new VaultTransitException(VaultFailure.Rejected, status, "Vault Transit returned an unwrapped key in a form that was not expected.");
        }
    }

    public void Dispose()
    {
        _checkGate.Dispose();
        _tokenGate.Dispose();
        _tokens.Dispose();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>Reads <c>transit/keys/&lt;key&gt;</c> once and refuses a key that is not derived or does not support encryption.</summary>
    private async ValueTask EnsureKeyCheckedAsync(CancellationToken cancellationToken)
    {
        if (_keyChecked)
        {
            return;
        }

        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keyChecked)
            {
                return;
            }

            (int status, byte[] response) = await SendAuthorizedAsync(HttpMethod.Get, $"v1/{_mount}/keys/{_key}", null, cancellationToken).ConfigureAwait(false);
            if (status != 200)
            {
                throw Failure(status, "key check");
            }

            KeyInfo? info = Parse(response, VaultJson.Default.KeyInfoResponse)?.Data;
            if (info is null)
            {
                throw new VaultTransitException(VaultFailure.Rejected, status, "Vault Transit returned key information in a form that was not expected.");
            }

            if (info.Derived != true)
            {
                throw new InvalidOperationException(
                    $"The Transit key '{_pinnedId}' is not a derived key. Create it with derived=true; without it Vault cannot bind a wrapped key to its context.");
            }

            if (info.SupportsEncryption != true)
            {
                throw new InvalidOperationException($"The Transit key '{_pinnedId}' does not support encryption (use an aes256-gcm96 or chacha20-poly1305 key).");
            }

            _keyChecked = true;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<(int Status, byte[] Body)> SendAuthorizedAsync(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken)
    {
        string token = await _tokens.GetAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        int status = 403;
        byte[] response = [];
        try
        {
            await EnsureTokenAllowedAsync(token, cancellationToken).ConfigureAwait(false);
            (status, response) = await SendAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
        }
        catch (VaultTransitException ex) when (ex.Failure == VaultFailure.Authentication)
        {
            // Treated like a 403: read the token again below; if it is still refused, that error is thrown.
        }

        if (status is 401 or 403)
        {
            // The agent may have renewed or replaced the token: read it again once.
            token = await _tokens.GetAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            await EnsureTokenAllowedAsync(token, cancellationToken).ConfigureAwait(false);
            (status, response) = await SendAsync(method, path, body, token, cancellationToken).ConfigureAwait(false);
        }

        return (status, response);
    }

    /// <summary>
    /// Asks Vault who the token is (<c>auth/token/lookup-self</c>) the first time this token is seen, and refuses a token with the <c>root</c> policy.
    /// A token that cannot look itself up is refused too: a token that cannot be checked is not trusted. A renewed token is a new value and is checked again.
    /// </summary>
    private async ValueTask EnsureTokenAllowedAsync(string token, CancellationToken cancellationToken)
    {
        if (string.Equals(_checkedToken, token, StringComparison.Ordinal))
        {
            return;
        }

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(_checkedToken, token, StringComparison.Ordinal))
            {
                return;
            }

            (int status, byte[] response) = await SendAsync(HttpMethod.Get, "v1/auth/token/lookup-self", null, token, cancellationToken).ConfigureAwait(false);
            if (status is 401 or 403)
            {
                throw new VaultTransitException(
                    VaultFailure.Authentication,
                    status,
                    "Vault refused the token's request to look itself up (auth/token/lookup-self), so it cannot be checked for the root policy and is not used. " +
                    "Either the token is not valid, or its policies lack: path \"auth/token/lookup-self\" { capabilities = [\"read\"] } (the default policy has it).");
            }

            if (status != 200)
            {
                throw Failure(status, "token check");
            }

            TokenLookup? data = Parse(response, VaultJson.Default.TokenLookupResponse)?.Data;
            if (data?.Policies is not { } policies)
            {
                throw new VaultTransitException(VaultFailure.Rejected, status, "Vault Transit returned token information in a form that was not expected.");
            }

            if (policies.Contains("root", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("The Vault token has the root policy. Use a token that carries only the policy for this key (see the key provider documentation).");
            }

            _checkedToken = token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    internal async Task<(int Status, byte[] Body)> SendAsync(HttpMethod method, string path, byte[]? body, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_options.Address!, path));
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Vault-Token", token);
        }

        if (_namespace.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("X-Vault-Namespace", _namespace);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            byte[] content = await ReadBoundedAsync(response, timeout.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, content);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new VaultTransitException(VaultFailure.Unavailable, 0, "Vault Transit did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new VaultTransitException(VaultFailure.Unavailable, 0, "Vault Transit could not be reached.", ex);
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        int max = _options.MaxResponseBytes;
        if (response.Content.Headers.ContentLength is > 0 and { } declared && declared > max)
        {
            throw new VaultTransitException(VaultFailure.Rejected, (int)response.StatusCode, "Vault Transit returned a response larger than the limit.");
        }

        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > max)
                {
                    throw new VaultTransitException(VaultFailure.Rejected, (int)response.StatusCode, "Vault Transit returned a response larger than the limit.");
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chunk);
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    private static T? Parse<T>(byte[] json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(json, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static VaultTransitException Failure(int status, string operation)
    {
        VaultFailure kind = status switch
        {
            401 or 403 => VaultFailure.Authentication,
            404 => VaultFailure.NotFound,
            >= 500 or 429 or 412 => VaultFailure.Unavailable,
            _ => VaultFailure.Rejected,
        };
        return new VaultTransitException(kind, status, $"Vault Transit {operation} failed: {kind} (HTTP {status.ToString(CultureInfo.InvariantCulture)}).");
    }

    /// <summary>Accepts <c>vault:v&lt;n&gt;:&lt;canonical base64&gt;</c> only. Vault's own decoder ignores spare bits in the last character, so a flipped bit there would go unnoticed.</summary>
    private static bool TryParseCiphertext(string text, out int version)
    {
        version = 0;
        Match m = CiphertextPattern().Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out version) || version == 0)
        {
            return false;
        }

        string b64 = m.Groups[2].Value;
        Span<byte> bytes = new byte[b64.Length];
        return Convert.TryFromBase64String(b64, bytes, out int written) && Convert.ToBase64String(bytes[..written]) == b64;
    }

    /// <summary>The stored id must be exactly the configured <c>mount/key</c>, with an optional <c>#version</c> (0 when absent).</summary>
    private bool TryParseKeyId(string id, out int version)
    {
        version = 0;
        int hash = id.LastIndexOf('#');
        string baseId = hash < 0 ? id : id[..hash];
        if (!string.Equals(baseId, _pinnedId, StringComparison.Ordinal))
        {
            return false;
        }

        return hash < 0 || (id.Length - hash - 1 is > 0 and <= 6 &&
            int.TryParse(id.AsSpan(hash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out version) && version > 0);
    }

    [GeneratedRegex(@"^vault:v([0-9]{1,6}):([A-Za-z0-9+/]+={0,2})\z")]
    private static partial Regex CiphertextPattern();

    // ----- tokens -----

    private sealed class TokenSource : IDisposable
    {
        private readonly VaultAuthMethod _method;
        private readonly VaultTransitKeyProvider _owner;
        private readonly TimeProvider _time;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private string? _token;
        private DateTimeOffset _expires = DateTimeOffset.MinValue;
        private DateTime _fileStamp;
        private long _fileLength = -1;
        private DateTimeOffset _lastStat = DateTimeOffset.MinValue;

        internal TokenSource(VaultAuthMethod method, VaultTransitKeyProvider owner, TimeProvider time)
        {
            _method = method;
            _owner = owner;
            _time = time;
        }

        public void Dispose() => _gate.Dispose();

        internal async ValueTask<string> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DateTimeOffset now = _time.GetUtcNow();
                switch (_method)
                {
                    case VaultAuthMethod.Source source:
                        return Check(await Call(source.Get, cancellationToken).ConfigureAwait(false));

                    case VaultAuthMethod.TokenFile file:
                        if (_token is null || forceRefresh || FileChanged(file.Path, now))
                        {
                            _token = Check(await ReadFileAsync(file.Path, cancellationToken).ConfigureAwait(false));
                        }

                        return _token;

                    default:
                        if (_token is null || forceRefresh || now >= _expires)
                        {
                            await LoginAsync(now, cancellationToken).ConfigureAwait(false);
                        }

                        return _token!;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool FileChanged(string path, DateTimeOffset now)
        {
            if (now - _lastStat < TimeSpan.FromSeconds(1))
            {
                return false;
            }

            _lastStat = now;
            try
            {
                var info = new FileInfo(path);
                return !info.Exists || info.LastWriteTimeUtc != _fileStamp || info.Length != _fileLength;
            }
            catch (IOException)
            {
                return true;
            }
        }

        private async Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 8192)
                {
                    throw new VaultTransitException(VaultFailure.Token, 0, "The Vault token file is missing or too large.");
                }

                _fileStamp = info.LastWriteTimeUtc;
                _fileLength = info.Length;
                return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new VaultTransitException(VaultFailure.Token, 0, "The Vault token file could not be read.", ex);
            }
        }

        private static async Task<string> Call(Func<CancellationToken, ValueTask<string>> get, CancellationToken cancellationToken)
        {
            try
            {
                return await get(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not VaultTransitException)
            {
                // The supplied function's own message is not passed on: it could contain what it was reading.
                throw new VaultTransitException(VaultFailure.Token, 0, "The Vault token source failed.", ex);
            }
        }

        private static string Check(string? raw)
        {
            string token = raw?.Trim() ?? string.Empty;
            if (token.Length is 0 or > 4096 || token.Any(static c => c < '!' || c > '~'))
            {
                throw new VaultTransitException(VaultFailure.Token, 0, "The Vault token is empty or malformed.");
            }

            return token;
        }

        private async Task LoginAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            string path;
            string json;
            switch (_method)
            {
                case VaultAuthMethod.Kubernetes k:
                    string jwt = Check(await ReadFileAsync(k.JwtPath, cancellationToken).ConfigureAwait(false));
                    path = $"v1/auth/{k.AuthMount}/login";
                    json = JsonSerializer.Serialize(new KubernetesLogin(k.Role, jwt), VaultJson.Default.KubernetesLogin);
                    break;
                case VaultAuthMethod.AppRole a:
                    string secret = Check(await Call(a.SecretId, cancellationToken).ConfigureAwait(false));
                    path = $"v1/auth/{a.AuthMount}/login";
                    json = JsonSerializer.Serialize(new AppRoleLogin(a.RoleId, secret), VaultJson.Default.AppRoleLogin);
                    break;
                default:
                    throw new InvalidOperationException();
            }

            (int status, byte[] body) = await _owner.SendAsync(HttpMethod.Post, path, Encoding.UTF8.GetBytes(json), null, cancellationToken).ConfigureAwait(false);
            if (status != 200)
            {
                VaultFailure kind = status is >= 500 or 429 or 412 ? VaultFailure.Unavailable : VaultFailure.Token;
                throw new VaultTransitException(kind, status, $"Vault sign-in failed (HTTP {status.ToString(CultureInfo.InvariantCulture)}).");
            }

            LoginResponse? parsed = Parse(body, VaultJson.Default.LoginResponse);
            _token = Check(parsed?.Auth?.ClientToken);
            long lease = parsed?.Auth?.LeaseDuration ?? 0;
            // Sign in again at 80 % of the lease; a lease of 0 means "does not expire", re-checked hourly (a 403 forces a new sign-in anyway).
            TimeSpan life = lease > 0 ? TimeSpan.FromSeconds(lease * 0.8) : TimeSpan.FromHours(1);
            _expires = now + life;
        }
    }

    // ----- wire types (source-generated JSON) -----

    internal sealed record EncryptRequest([property: JsonPropertyName("plaintext")] string Plaintext, [property: JsonPropertyName("context")] string Context);

    internal sealed record DecryptRequest([property: JsonPropertyName("ciphertext")] string Ciphertext, [property: JsonPropertyName("context")] string Context);

    internal sealed record EncryptData([property: JsonPropertyName("ciphertext")] string? Ciphertext);

    internal sealed record EncryptResponse([property: JsonPropertyName("data")] EncryptData? Data);

    internal sealed record DecryptData([property: JsonPropertyName("plaintext")] string? Plaintext);

    internal sealed record DecryptResponse([property: JsonPropertyName("data")] DecryptData? Data);

    internal sealed record KeyInfo(
        [property: JsonPropertyName("derived")] bool? Derived,
        [property: JsonPropertyName("supports_encryption")] bool? SupportsEncryption);

    internal sealed record TokenLookup([property: JsonPropertyName("policies")] string[]? Policies);

    internal sealed record TokenLookupResponse([property: JsonPropertyName("data")] TokenLookup? Data);

    internal sealed record KeyInfoResponse([property: JsonPropertyName("data")] KeyInfo? Data);

    internal sealed record KubernetesLogin([property: JsonPropertyName("role")] string Role, [property: JsonPropertyName("jwt")] string Jwt);

    internal sealed record AppRoleLogin([property: JsonPropertyName("role_id")] string RoleId, [property: JsonPropertyName("secret_id")] string SecretId);

    internal sealed record LoginAuth(
        [property: JsonPropertyName("client_token")] string? ClientToken,
        [property: JsonPropertyName("lease_duration")] long? LeaseDuration);

    internal sealed record LoginResponse([property: JsonPropertyName("auth")] LoginAuth? Auth);

    [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
    [JsonSerializable(typeof(EncryptRequest))]
    [JsonSerializable(typeof(DecryptRequest))]
    [JsonSerializable(typeof(EncryptResponse))]
    [JsonSerializable(typeof(DecryptResponse))]
    [JsonSerializable(typeof(KeyInfoResponse))]
    [JsonSerializable(typeof(TokenLookupResponse))]
    [JsonSerializable(typeof(KubernetesLogin))]
    [JsonSerializable(typeof(AppRoleLogin))]
    [JsonSerializable(typeof(LoginResponse))]
    internal sealed partial class VaultJson : JsonSerializerContext;
}
