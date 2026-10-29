using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Kairos.Cloud.SelfHosted;

namespace Kairos.SelfHosted.Tests;

/// <summary>
/// One Vault-compatible server in dev mode with a Transit mount, a derived key and a token that carries only the documented
/// least-privilege policy. The root token exists only inside the test container and only to set that up.
/// </summary>
public abstract class TransitServerFixture : IAsyncLifetime
{
    public const string KeyName = "kairos";
    public const string PlainKeyName = "plain";
    private static readonly string[] KairosPolicy = ["kairos"];
    private static readonly string[] NoLookup = ["kairos-nolookup"];
    private const string RootToken = "kairos-test-dev-root";
    private IContainer? _container;
    private string? _directory;

    protected abstract string Image { get; }

    protected abstract string[] Environment { get; }

    public Uri Address { get; private set; } = null!;

    public string TokenFile { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        ContainerBuilder builder = new ContainerBuilder(Image)
            .WithPortBinding(8200, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8200).ForPath("/v1/sys/health")));
        foreach (string pair in Environment)
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            builder = builder.WithEnvironment(pair[..eq], pair[(eq + 1)..]);
        }

        _container = builder.Build();
        await _container.StartAsync();
        Address = new Uri($"http://127.0.0.1:{_container.GetMappedPublicPort(8200)}");

        using var http = new HttpClient { BaseAddress = Address };
        http.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);
        await Ok(http.PostAsJsonAsync("v1/sys/mounts/transit", new { type = "transit" }));
        await Ok(http.PostAsJsonAsync($"v1/transit/keys/{KeyName}", new { derived = true }));
        await Ok(http.PostAsJsonAsync($"v1/transit/keys/{PlainKeyName}", new { derived = false }));
        await Ok(http.PutAsJsonAsync("v1/sys/policies/acl/kairos", new { policy = Policy }));
        await Ok(http.PutAsJsonAsync("v1/sys/policies/acl/kairos-nolookup", new { policy = PolicyWithoutLookup }));

        _directory = Path.Combine(Path.GetTempPath(), "kairos-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        // The documented policy, with the default policy left out: what the documentation recommends must work on its own.
        TokenFile = await WriteTokenAsync(http, "token", await CreateTokenAsync(http, KairosPolicy, noDefault: true));
        RootTokenFile = await WriteTokenAsync(http, "root", RootToken);
        // No lookup-self permission at all: refused.
        NoLookupTokenFile = await WriteTokenAsync(http, "nolookup", await CreateTokenAsync(http, NoLookup, noDefault: true));
        // The same policy plus the default policy, which carries lookup-self: works. This is the claim made in the documentation.
        DefaultPolicyTokenFile = await WriteTokenAsync(http, "default", await CreateTokenAsync(http, NoLookup, noDefault: false));
    }

    public string RootTokenFile { get; private set; } = null!;

    public string NoLookupTokenFile { get; private set; } = null!;

    public string DefaultPolicyTokenFile { get; private set; } = null!;

    private async Task<string> WriteTokenAsync(HttpClient http, string name, string token)
    {
        string path = Path.Combine(_directory!, name);
        await File.WriteAllTextAsync(path, token);
        return path;
    }

    private static async Task<string> CreateTokenAsync(HttpClient http, string[] policies, bool noDefault)
    {
        HttpResponseMessage created = await http.PostAsJsonAsync("v1/auth/token/create", new { policies, ttl = "1h", no_default_policy = noDefault });
        created.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("auth").GetProperty("client_token").GetString()!;
    }

    public VaultTransitKeyProvider ProviderWithToken(string tokenFile) =>
        new(new VaultTransitOptions { Address = Address, KeyName = KeyName }.UseTokenFile(tokenFile));

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        if (_directory is not null && Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    public VaultTransitOptions Options(string key = KeyName) =>
        new VaultTransitOptions { Address = Address, KeyName = key }.UseTokenFile(TokenFile);

    public VaultTransitKeyProvider Provider(string key = KeyName) => new(Options(key));

    /// <summary>Rotates the Transit key (a new key version); only possible with the root token, which the tests hold.</summary>
    public async Task RotateAsync(string key = KeyName)
    {
        using var http = new HttpClient { BaseAddress = Address };
        http.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);
        await Ok(http.PostAsync($"v1/transit/keys/{key}/rotate", null));
    }

    private static async Task Ok(Task<HttpResponseMessage> call)
    {
        using HttpResponseMessage response = await call;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Setting up the test server failed with HTTP {(int)response.StatusCode}.");
        }
    }

    // The same least-privilege policy as the documentation (plus the plain key, used to prove it is refused).
    private const string PolicyWithoutLookup = """
        path "transit/keys/kairos"    { capabilities = ["read"] }
        path "transit/encrypt/kairos" { capabilities = ["update"] }
        path "transit/decrypt/kairos" { capabilities = ["update"] }
        """;

    private const string Policy = """
        path "auth/token/lookup-self" { capabilities = ["read"] }
        path "transit/keys/kairos"    { capabilities = ["read"] }
        path "transit/keys/plain"     { capabilities = ["read"] }
        path "transit/encrypt/kairos" { capabilities = ["update"] }
        path "transit/decrypt/kairos" { capabilities = ["update"] }
        path "transit/encrypt/plain"  { capabilities = ["update"] }
        """;
}

public sealed class VaultServerFixture : TransitServerFixture
{
    protected override string Image => System.Environment.GetEnvironmentVariable("KAIROS_VAULT_IMAGE") is { Length: > 0 } i ? i : "hashicorp/vault:1.18";

    protected override string[] Environment =>
        ["VAULT_DEV_ROOT_TOKEN_ID=kairos-test-dev-root", "VAULT_DEV_LISTEN_ADDRESS=0.0.0.0:8200"];
}

public sealed class OpenBaoServerFixture : TransitServerFixture
{
    protected override string Image => System.Environment.GetEnvironmentVariable("KAIROS_OPENBAO_IMAGE") is { Length: > 0 } i ? i : "openbao/openbao:2.7.2";

    protected override string[] Environment =>
        ["BAO_DEV_ROOT_TOKEN_ID=kairos-test-dev-root", "BAO_DEV_LISTEN_ADDRESS=0.0.0.0:8200"];
}
