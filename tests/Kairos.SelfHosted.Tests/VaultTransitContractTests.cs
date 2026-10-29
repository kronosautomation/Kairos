using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairos.Cloud.SelfHosted;

namespace Kairos.SelfHosted.Tests;

/// <summary>Contract tests for <see cref="VaultTransitKeyProvider"/> against a fake HTTP handler: what is sent, what is refused, and what a failure may say.</summary>
public sealed class VaultTransitContractTests : IDisposable
{
    private const string Token = "fake-token-AAAA1111";
    private static readonly byte[] Context = "tenant-a/ns-1"u8.ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kairos-vtc-" + Guid.NewGuid().ToString("N"));
    private readonly string _tokenFile;

    public VaultTransitContractTests()
    {
        Directory.CreateDirectory(_dir);
        _tokenFile = Path.Combine(_dir, "token");
        File.WriteAllText(_tokenFile, Token + "\n");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ----- a small fake Transit server that models derived keys: ciphertext = vault:v<version>:base64(context-length | context | plaintext) -----

    private sealed class FakeVault : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Token, string? Namespace, string Body)> Requests { get; } = [];

        public bool Derived { get; set; } = true;

        public bool SupportsEncryption { get; set; } = true;

        public int Version { get; set; } = 1;

        public Func<(string Path, string? Token), HttpResponseMessage?>? Override { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            string? token = request.Headers.TryGetValues("X-Vault-Token", out IEnumerable<string>? t) ? t.Single() : null;
            string? ns = request.Headers.TryGetValues("X-Vault-Namespace", out IEnumerable<string>? n) ? n.Single() : null;
            Requests.Add((request.Method, path, token, ns, body));
            if (Override?.Invoke((path, token)) is { } custom)
            {
                return custom;
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/transit/keys/", StringComparison.Ordinal))
            {
                return Json(200, $$$"""{"data":{"derived":{{{Derived.ToString().ToLowerInvariant()}}},"supports_encryption":{{{SupportsEncryption.ToString().ToLowerInvariant()}}},"type":"aes256-gcm96"}}""");
            }

            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("context", out JsonElement ctx))
            {
                return Json(400, """{"errors":["missing 'context'"]}""");
            }

            if (path.StartsWith("/v1/transit/encrypt/", StringComparison.Ordinal))
            {
                byte[] plain = Convert.FromBase64String(root.GetProperty("plaintext").GetString()!);
                byte[] c = Convert.FromBase64String(ctx.GetString()!);
                string sealedText = Convert.ToBase64String([(byte)c.Length, .. c, .. plain]);
                return Json(200, $$$"""{"data":{"ciphertext":"vault:v{{{Version}}}:{{{sealedText}}}","key_version":{{{Version}}}}}""");
            }

            string ciphertext = root.GetProperty("ciphertext").GetString()!;
            byte[] raw = Convert.FromBase64String(ciphertext[(ciphertext.IndexOf(':', 7) + 1)..]);
            byte[] want = Convert.FromBase64String(ctx.GetString()!);
            if (raw[0] != want.Length || !raw.AsSpan(1, raw[0]).SequenceEqual(want))
            {
                return Json(400, """{"errors":["cipher: message authentication failed"]}""");
            }

            return Json(200, $$$"""{"data":{"plaintext":"{{{Convert.ToBase64String(raw.AsSpan(1 + raw[0]))}}}"}}""");
        }
    }

    private static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private VaultTransitOptions Options() => new VaultTransitOptions { Address = new Uri("https://vault.example.test:8200"), KeyName = "kairos" }.UseTokenFile(_tokenFile);

    private VaultTransitKeyProvider Provider(FakeVault fake, VaultTransitOptions? options = null, TimeProvider? time = null) =>
        new(options ?? Options(), new HttpClient(fake), time);

    private static async Task<WrappedKey> WrapAsync(VaultTransitKeyProvider p) =>
        await p.WrapAsync(RandomNumberGenerator.GetBytes(32), Context, CancellationToken.None);

    // ----- what is sent -----

    [Fact]
    public async Task The_context_the_key_and_the_token_are_sent_and_the_namespace_when_set()
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake, Options().WithNamespace("team-a"));
        byte[] dataKey = RandomNumberGenerator.GetBytes(32);

        WrappedKey wrapped = await provider.WrapAsync(dataKey, Context, CancellationToken.None);

        var encrypt = fake.Requests.Single(r => r.Path == "/v1/transit/encrypt/kairos");
        using JsonDocument body = JsonDocument.Parse(encrypt.Body);
        Assert.Equal(Convert.ToBase64String(Context), body.RootElement.GetProperty("context").GetString());
        Assert.Equal(Convert.ToBase64String(dataKey), body.RootElement.GetProperty("plaintext").GetString());
        Assert.All(fake.Requests, r => Assert.Equal(Token, r.Token));
        Assert.All(fake.Requests, r => Assert.Equal("team-a", r.Namespace));
        Assert.Equal("transit/kairos#1", wrapped.KeyId);

        Assert.Equal(dataKey, await provider.UnwrapAsync(wrapped, Context, CancellationToken.None));
        using JsonDocument decrypt = JsonDocument.Parse(fake.Requests.Last().Body);
        Assert.Equal(Convert.ToBase64String(Context), decrypt.RootElement.GetProperty("context").GetString());
    }

    [Fact]
    public async Task A_context_of_another_tenant_cannot_unwrap_what_was_wrapped_for_the_first()
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);
        WrappedKey wrapped = await WrapAsync(provider);

        await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(wrapped, "tenant-b/ns-1"u8.ToArray(), CancellationToken.None));
        await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(wrapped, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task Wrapping_without_a_context_is_refused_before_any_request()
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);

        await Assert.ThrowsAsync<ArgumentException>(async () => await provider.WrapAsync(new byte[32], ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        Assert.Empty(fake.Requests);
    }

    // ----- the key must be derived -----

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_key_that_is_not_derived_or_cannot_encrypt_is_refused_on_first_use(bool derived, bool encrypts)
    {
        var fake = new FakeVault { Derived = derived, SupportsEncryption = encrypts };
        using VaultTransitKeyProvider provider = Provider(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await WrapAsync(provider));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await provider.UnwrapAsync(new WrappedKey("transit/kairos#1", "vault:v1:AAAA"u8.ToArray()), Context, CancellationToken.None));
        Assert.DoesNotContain(fake.Requests, r => r.Path.Contains("/encrypt/", StringComparison.Ordinal) || r.Path.Contains("/decrypt/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_key_is_read_once_not_on_every_call()
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);

        for (int i = 0; i < 5; i++)
        {
            await WrapAsync(provider);
        }

        Assert.Equal(1, fake.Requests.Count(r => r.Method == HttpMethod.Get));
    }

    // ----- key id pinning -----

    [Theory]
    [InlineData("transit/other")]
    [InlineData("other/kairos")]
    [InlineData("transit/kairos/../other")]
    [InlineData("transit/kairos/extra")]
    [InlineData("../sys/seal")]
    [InlineData("transit/kairos#0")]
    [InlineData("transit/kairos#abc")]
    [InlineData("transit/kairos#1#2")]
    [InlineData("transit/kairos#")]
    [InlineData("TRANSIT/KAIROS")]
    [InlineData("transit/kairos?x=1")]
    [InlineData("transit/kairos\n")]
    [InlineData("")]
    public async Task A_stored_key_id_that_is_not_the_configured_mount_and_key_is_refused_without_any_request(string keyId)
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);
        WrappedKey good = await WrapAsync(provider);
        fake.Requests.Clear();

        await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(good with { KeyId = keyId }, Context, CancellationToken.None));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task A_key_version_in_the_id_that_disagrees_with_the_ciphertext_is_refused()
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);
        WrappedKey good = await WrapAsync(provider);

        await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(good with { KeyId = "transit/kairos#2" }, Context, CancellationToken.None));
    }

    [Theory]
    [InlineData("vault:v1:AAAA\n")]
    [InlineData("vault:v1:AAAA/../../sys")]
    [InlineData("vault:v0:AAAA")]
    [InlineData("vault:v1:")]
    [InlineData("hvs:v1:AAAA")]
    [InlineData("vault:v1:AA=A")]
    public async Task A_ciphertext_that_is_not_in_the_exact_vault_form_is_refused_without_a_request(string ciphertext)
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);
        await Assert.ThrowsAsync<KeyUnwrapException>(async () =>
            await provider.UnwrapAsync(new WrappedKey("transit/kairos#1", Encoding.UTF8.GetBytes(ciphertext)), Context, CancellationToken.None));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Base64_with_spare_bits_set_in_the_last_character_is_refused_because_the_server_would_ignore_them()
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);
        WrappedKey good = await WrapAsync(provider);
        string text = Encoding.ASCII.GetString(good.Ciphertext);
        string b64 = text[(text.LastIndexOf(':') + 1)..];
        Assert.EndsWith("=", b64, StringComparison.Ordinal); // 1 + 32 + 13 bytes = 46: padded
        char last = b64[^3];
        string altered = text[..text.LastIndexOf(':')] + ":" + b64[..^3] + (char)(last ^ 0x01) + b64[^2..];

        await Assert.ThrowsAsync<KeyUnwrapException>(async () =>
            await provider.UnwrapAsync(good with { Ciphertext = Encoding.ASCII.GetBytes(altered) }, Context, CancellationToken.None));
    }

    // ----- options: addresses, names, tokens -----

    [Theory]
    [InlineData("transit", "k/../x")]
    [InlineData("transit", "k?x=1")]
    [InlineData("transit", "k/x")]
    [InlineData("transit", "k%2e%2e")]
    [InlineData("transit", "k#frag")]
    [InlineData("transit", "k x")]
    [InlineData("transit", "")]
    [InlineData("transit", "k\n")]
    [InlineData("../sys", "k")]
    [InlineData("transit/..", "k")]
    [InlineData("/transit", "k")]
    [InlineData("transit/", "k")]
    [InlineData("tran sit", "k")]
    [InlineData("transit?x=1", "k")]
    [InlineData("a/b/c/d/e", "k")]
    [InlineData("sys%2fseal", "k")]
    public void Mounts_and_key_names_that_could_change_the_request_path_are_refused(string mount, string key)
    {
        VaultTransitOptions options = Options();
        options.Mount = mount;
        options.KeyName = key;

        Assert.Throws<InvalidOperationException>(() => new VaultTransitKeyProvider(options, new HttpClient(new FakeVault())));
    }

    [Theory]
    [InlineData("http://vault.example.test:8200")]
    [InlineData("ftp://vault.example.test")]
    [InlineData("https://user:pw@vault.example.test")]
    [InlineData("https://vault.example.test/v1/sys")]
    [InlineData("https://vault.example.test/?x=1")]
    [InlineData("http://127.0.0.1.example.test:8200")]
    public void Addresses_that_are_not_https_or_loopback_or_have_extras_are_refused(string address)
    {
        VaultTransitOptions options = Options();
        options.Address = new Uri(address);

        Assert.Throws<InvalidOperationException>(() => new VaultTransitKeyProvider(options, new HttpClient(new FakeVault())));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8200")]
    [InlineData("http://localhost:8200")]
    [InlineData("http://[::1]:8200")]
    [InlineData("https://vault.example.test:8200/")]
    public void Https_and_loopback_addresses_are_accepted(string address)
    {
        VaultTransitOptions options = Options();
        options.Address = new Uri(address);

        using var provider = new VaultTransitKeyProvider(options, new HttpClient(new FakeVault()));
    }

    [Fact]
    public void A_missing_authentication_method_or_two_of_them_are_refused()
    {
        var none = new VaultTransitOptions { Address = new Uri("https://v.example.test"), KeyName = "k" };
        Assert.Throws<InvalidOperationException>(() => new VaultTransitKeyProvider(none, new HttpClient(new FakeVault())));
        Assert.Throws<InvalidOperationException>(() => Options().UseTokenSource(_ => ValueTask.FromResult("x")));
    }

    [Theory]
    [InlineData("hvs.CAESIFAKEFAKEFAKEFAKE")]
    [InlineData("hvb.CAESIFAKEFAKEFAKEFAKE")]
    [InlineData("s.FAKEFAKEFAKEFAKEFAKEFAKE")]
    [InlineData("line one\nline two")]
    public void A_token_given_as_text_where_a_file_path_belongs_is_refused_and_not_repeated(string text)
    {
        var options = new VaultTransitOptions();

        ArgumentException ex = Assert.Throws<ArgumentException>(() => options.UseTokenFile(text));
        Assert.DoesNotContain(text, ex.ToString(), StringComparison.Ordinal);
        ArgumentException ex2 = Assert.Throws<ArgumentException>(() => options.UseAppRole("role-id", text));
        Assert.DoesNotContain(text, ex2.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_options_have_no_member_that_takes_a_token_as_text()
    {
        foreach (System.Reflection.MethodInfo m in typeof(VaultTransitOptions).GetMethods().Where(m => m.DeclaringType == typeof(VaultTransitOptions)))
        {
            Assert.DoesNotContain("Token", m.Name.Replace("TokenFile", string.Empty, StringComparison.Ordinal).Replace("TokenSource", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(typeof(VaultTransitOptions).GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
    }

    // ----- error mapping; no token in any message -----

    [Theory]
    [InlineData(401, VaultFailure.Authentication)]
    [InlineData(403, VaultFailure.Authentication)]
    [InlineData(404, VaultFailure.NotFound)]
    [InlineData(429, VaultFailure.Unavailable)]
    [InlineData(500, VaultFailure.Unavailable)]
    [InlineData(503, VaultFailure.Unavailable)]
    [InlineData(400, VaultFailure.Rejected)]
    [InlineData(307, VaultFailure.Rejected)]
    public async Task Failures_on_wrap_are_typed_and_never_contain_the_token_even_when_the_server_echoes_it(int status, VaultFailure expected)
    {
        var fake = new FakeVault
        {
            Override = r => r.Path.Contains("/encrypt/", StringComparison.Ordinal) ? Json(status, $$$"""{"errors":["bad token {{{r.Token}}}"]}""") : null,
        };
        using VaultTransitKeyProvider provider = Provider(fake);

        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () => await WrapAsync(provider));

        Assert.Equal(expected, ex.Failure);
        Assert.Equal(status, ex.StatusCode);
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fake-token", ex.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    public async Task On_unwrap_a_400_or_404_is_the_same_unwrap_failure_as_a_wrong_context(int status)
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);
        WrappedKey wrapped = await WrapAsync(provider);
        fake.Override = r => r.Path.Contains("/decrypt/", StringComparison.Ordinal) ? Json(status, $$$"""{"errors":["{{{r.Token}}}"]}""") : null;

        KeyUnwrapException ex = await Assert.ThrowsAsync<KeyUnwrapException>(async () => await provider.UnwrapAsync(wrapped, Context, CancellationToken.None));
        Assert.Equal(new KeyUnwrapException().Message, ex.Message);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task On_unwrap_an_outage_or_a_permission_problem_is_not_disguised_as_a_bad_key(int status)
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);
        WrappedKey wrapped = await WrapAsync(provider);
        fake.Override = r => r.Path.Contains("/decrypt/", StringComparison.Ordinal) ? Json(status, "{}") : null;

        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () => await provider.UnwrapAsync(wrapped, Context, CancellationToken.None));
        Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_network_failure_and_a_timeout_are_unavailable_and_say_nothing_about_the_token()
    {
        using var failing = new VaultTransitKeyProvider(Options(), new HttpClient(new Throwing(new HttpRequestException($"connection refused for {Token}"))));
        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () => await WrapAsync(failing));
        Assert.Equal(VaultFailure.Unavailable, ex.Failure);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal); // the message; the inner exception is the framework's own

        VaultTransitOptions options = Options();
        options.RequestTimeout = TimeSpan.FromMilliseconds(50);
        using var slow = new VaultTransitKeyProvider(options, new HttpClient(new Hanging()));
        VaultTransitException timeout = await Assert.ThrowsAsync<VaultTransitException>(async () => await WrapAsync(slow));
        Assert.Equal(VaultFailure.Unavailable, timeout.Failure);
    }

    [Fact]
    public async Task A_response_larger_than_the_limit_is_refused()
    {
        var fake = new FakeVault { Override = r => r.Path.Contains("/encrypt/", StringComparison.Ordinal) ? Json(200, new string('x', 100_000)) : null };
        using VaultTransitKeyProvider provider = Provider(fake);

        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () => await WrapAsync(provider));
        Assert.Equal(VaultFailure.Rejected, ex.Failure);
    }

    [Fact]
    public async Task A_token_source_that_throws_with_the_token_in_its_message_does_not_leak_it()
    {
        VaultTransitOptions options = new VaultTransitOptions { Address = new Uri("https://v.example.test"), KeyName = "kairos" }
            .UseTokenSource(_ => throw new InvalidOperationException($"cannot read {Token}"));
        using var provider = new VaultTransitKeyProvider(options, new HttpClient(new FakeVault()));

        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () => await WrapAsync(provider));
        Assert.Equal(VaultFailure.Token, ex.Failure);
        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("two words")]
    [InlineData("tab\there")]
    public async Task An_empty_or_malformed_token_file_is_refused_without_a_request_and_without_repeating_it(string content)
    {
        await File.WriteAllTextAsync(_tokenFile, content);
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake);

        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () => await WrapAsync(provider));
        Assert.Equal(VaultFailure.Token, ex.Failure);
        Assert.Empty(fake.Requests);
        if (content.Trim().Length > 0)
        {
            Assert.DoesNotContain(content.Trim(), ex.ToString(), StringComparison.Ordinal);
        }
    }

    // ----- token renewal -----

    [Fact]
    public async Task A_renewed_token_in_the_file_is_picked_up_when_the_file_changes()
    {
        var time = new ManualTime();
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake, time: time);
        await WrapAsync(provider);
        Assert.All(fake.Requests, r => Assert.Equal(Token, r.Token));

        await File.WriteAllTextAsync(_tokenFile, "fake-token-BBBB2222-renewed");
        File.SetLastWriteTimeUtc(_tokenFile, DateTime.UtcNow.AddMinutes(5));
        time.Advance(TimeSpan.FromSeconds(2));
        fake.Requests.Clear();
        await WrapAsync(provider);

        Assert.All(fake.Requests, r => Assert.Equal("fake-token-BBBB2222-renewed", r.Token));
    }

    [Fact]
    public async Task A_403_makes_the_provider_read_the_token_again_at_once_and_retry_once()
    {
        var fake = new FakeVault();
        using VaultTransitKeyProvider provider = Provider(fake, time: new ManualTime());
        await WrapAsync(provider);
        await File.WriteAllTextAsync(_tokenFile, "fake-token-CCCC3333-after-renewal");
        fake.Requests.Clear();
        fake.Override = r => r.Token == Token ? Json(403, """{"errors":["permission denied"]}""") : null;

        await WrapAsync(provider);

        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(Token, fake.Requests[0].Token);
        Assert.Equal("fake-token-CCCC3333-after-renewal", fake.Requests[1].Token);
    }

    [Fact]
    public async Task A_persistent_403_is_tried_twice_and_then_reported_as_authentication()
    {
        var fake = new FakeVault { Override = r => r.Path.Contains("/encrypt/", StringComparison.Ordinal) ? Json(403, "{}") : null };
        using VaultTransitKeyProvider provider = Provider(fake);

        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () => await WrapAsync(provider));

        Assert.Equal(VaultFailure.Authentication, ex.Failure);
        Assert.Equal(2, fake.Requests.Count(r => r.Path.Contains("/encrypt/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Kubernetes_login_sends_the_service_account_jwt_caches_the_token_and_signs_in_again_after_a_403()
    {
        string jwtFile = Path.Combine(_dir, "jwt");
        await File.WriteAllTextAsync(jwtFile, "fake-service-account-jwt");
        var logins = 0;
        var fake = new FakeVault();
        fake.Override = r =>
        {
            if (r.Path == "/v1/auth/kubernetes/login")
            {
                logins++;
                return Json(200, $$$"""{"auth":{"client_token":"fake-login-token-{{{logins}}}","lease_duration":3600}}""");
            }

            return null;
        };
        VaultTransitOptions options = new VaultTransitOptions { Address = new Uri("https://v.example.test"), KeyName = "kairos" }.UseKubernetesAuth("kairos-role", jwtFile);
        using var provider = new VaultTransitKeyProvider(options, new HttpClient(fake), new ManualTime());

        await WrapAsync(provider);
        await WrapAsync(provider);
        Assert.Equal(1, logins);
        var login = fake.Requests.First(r => r.Path == "/v1/auth/kubernetes/login");
        using JsonDocument body = JsonDocument.Parse(login.Body);
        Assert.Equal("kairos-role", body.RootElement.GetProperty("role").GetString());
        Assert.Equal("fake-service-account-jwt", body.RootElement.GetProperty("jwt").GetString());
        Assert.Null(login.Token);
        Assert.Equal("fake-login-token-1", fake.Requests.First(r => r.Path.Contains("/encrypt/", StringComparison.Ordinal)).Token);

        fake.Override = r =>
        {
            if (r.Path == "/v1/auth/kubernetes/login")
            {
                logins++;
                return Json(200, $$$"""{"auth":{"client_token":"fake-login-token-{{{logins}}}","lease_duration":3600}}""");
            }

            return r.Token == "fake-login-token-1" ? Json(403, "{}") : null;
        };
        fake.Requests.Clear();
        await WrapAsync(provider);
        Assert.Equal(2, logins);
        Assert.Equal("fake-login-token-2", fake.Requests.Last().Token);
    }

    [Fact]
    public async Task AppRole_login_sends_the_role_id_and_secret_id_and_a_refused_login_does_not_repeat_them()
    {
        var fake = new FakeVault { Override = r => r.Path == "/v1/auth/approle/login" ? Json(400, """{"errors":["invalid secret id fake-secret-id"]}""") : null };
        VaultTransitOptions options = new VaultTransitOptions { Address = new Uri("https://v.example.test"), KeyName = "kairos" }
            .UseAppRole("role-id-1", _ => ValueTask.FromResult("fake-secret-id"));
        using var provider = new VaultTransitKeyProvider(options, new HttpClient(fake));

        VaultTransitException ex = await Assert.ThrowsAsync<VaultTransitException>(async () => await WrapAsync(provider));

        Assert.Equal(VaultFailure.Token, ex.Failure);
        Assert.DoesNotContain("fake-secret-id", ex.ToString(), StringComparison.Ordinal);
        using JsonDocument body = JsonDocument.Parse(fake.Requests.Single().Body);
        Assert.Equal("role-id-1", body.RootElement.GetProperty("role_id").GetString());
        Assert.Equal("fake-secret-id", body.RootElement.GetProperty("secret_id").GetString());
    }

    private sealed class Throwing(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }

    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

internal static class VaultTransitOptionsTestExtensions
{
    public static VaultTransitOptions WithNamespace(this VaultTransitOptions options, string ns)
    {
        options.Namespace = ns;
        return options;
    }
}
