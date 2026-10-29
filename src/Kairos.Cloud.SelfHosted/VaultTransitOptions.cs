using System.Net;
using System.Text.RegularExpressions;

namespace Kairos.Cloud.SelfHosted;

/// <summary>
/// Settings for <see cref="VaultTransitKeyProvider"/> (HashiCorp Vault and OpenBao). There is deliberately no way to pass a token as
/// text: a token comes from a file (the Vault Agent sink), from Kubernetes or AppRole login, or from a function you supply.
/// </summary>
public sealed partial class VaultTransitOptions
{
    /// <summary>The Vault address, for example <c>https://vault.example.com:8200</c>. Plain http is accepted for loopback addresses only.</summary>
    public Uri? Address { get; set; }

    /// <summary>The Transit secrets engine mount, one or more segments of letters, digits, '-' and '_' separated by '/'. Default <c>transit</c>.</summary>
    public string Mount { get; set; } = "transit";

    /// <summary>The Transit key name (letters, digits, '-' and '_'). The key must be a derived key that supports encryption.</summary>
    public string? KeyName { get; set; }

    /// <summary>Optional Vault Enterprise namespace, sent as <c>X-Vault-Namespace</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>Time allowed for one request to Vault. Default 10 seconds.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The most bytes read from any Vault response; a larger response is refused. Default 64 KiB.</summary>
    public int MaxResponseBytes { get; set; } = 64 * 1024;

    internal VaultAuthMethod? Auth { get; private set; }

    /// <summary>
    /// Reads the token from a file, typically the sink of a Vault Agent that renews it. The file is read again when it changes and when Vault
    /// answers 403, so the agent's renewals are picked up. Use a token with a policy that allows only what is described in the documentation,
    /// never a root token.
    /// </summary>
    public VaultTransitOptions UseTokenFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (LooksLikeToken(path))
        {
            throw new ArgumentException("This looks like a token, not a file path. A token is never accepted as text; give the path of a file that holds it.", nameof(path));
        }

        return SetAuth(new VaultAuthMethod.TokenFile(path));
    }

    /// <summary>
    /// Takes the token from a function you supply. It is called for each request, so cache inside it; it is called again (and its result used)
    /// when Vault answers 403.
    /// </summary>
    public VaultTransitOptions UseTokenSource(Func<CancellationToken, ValueTask<string>> tokenSource)
    {
        ArgumentNullException.ThrowIfNull(tokenSource);
        return SetAuth(new VaultAuthMethod.Source(tokenSource));
    }

    /// <summary>Signs in with the Kubernetes auth method using the pod's service account token file.</summary>
    public VaultTransitOptions UseKubernetesAuth(string role, string serviceAccountTokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token", string authMount = "kubernetes")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAccountTokenPath);
        ValidateName(role, nameof(role));
        ValidateMount(authMount, nameof(authMount));
        return SetAuth(new VaultAuthMethod.Kubernetes(role, serviceAccountTokenPath, authMount));
    }

    /// <summary>Signs in with AppRole. The secret id is read from a file or a function; it is never accepted as text.</summary>
    public VaultTransitOptions UseAppRole(string roleId, Func<CancellationToken, ValueTask<string>> secretIdSource, string authMount = "approle")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(secretIdSource);
        ValidateMount(authMount, nameof(authMount));
        return SetAuth(new VaultAuthMethod.AppRole(roleId, secretIdSource, authMount));
    }

    /// <summary>Signs in with AppRole; the secret id is read from <paramref name="secretIdFile"/> at each sign-in.</summary>
    public VaultTransitOptions UseAppRole(string roleId, string secretIdFile, string authMount = "approle")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretIdFile);
        if (LooksLikeToken(secretIdFile))
        {
            throw new ArgumentException("This looks like a token, not a file path. A secret id is never accepted as text.", nameof(secretIdFile));
        }

        return UseAppRole(roleId, async ct => (await File.ReadAllTextAsync(secretIdFile, ct).ConfigureAwait(false)).Trim(), authMount);
    }

    internal void Validate()
    {
        if (Address is null || !Address.IsAbsoluteUri || !string.IsNullOrEmpty(Address.UserInfo) || !string.IsNullOrEmpty(Address.Query) || !string.IsNullOrEmpty(Address.Fragment) ||
            (Address.AbsolutePath != "/" && !string.IsNullOrEmpty(Address.AbsolutePath)))
        {
            throw new InvalidOperationException("Vault Transit needs an absolute address of the form https://host:port with no user, path, query or fragment.");
        }

        if (Address.Scheme == Uri.UriSchemeHttps)
        {
            // fine
        }
        else if (Address.Scheme == Uri.UriSchemeHttp)
        {
            if (!IsLoopback(Address))
            {
                throw new InvalidOperationException("Vault Transit requires https unless the address is loopback.");
            }
        }
        else
        {
            throw new InvalidOperationException("Vault Transit requires an https address.");
        }

        ValidateMount(Mount, nameof(Mount));
        ValidateName(KeyName, nameof(KeyName));
        if (Namespace is not null && !NamespacePattern().IsMatch(Namespace))
        {
            throw new InvalidOperationException("The Vault namespace has characters that are not allowed.");
        }

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("RequestTimeout must be above zero and at most five minutes.");
        }

        if (MaxResponseBytes is < 1024 or > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException("MaxResponseBytes must be between 1 KiB and 4 MiB.");
        }

        if (Auth is null)
        {
            throw new InvalidOperationException("Vault Transit needs an authentication method: UseTokenFile, UseTokenSource, UseKubernetesAuth or UseAppRole.");
        }
    }

    internal static void ValidateMount(string? mount, string name)
    {
        if (mount is null || !MountPattern().IsMatch(mount))
        {
            throw new InvalidOperationException($"{name} must be one to four segments of letters, digits, '-' and '_' separated by '/'.");
        }
    }

    internal static void ValidateName(string? value, string name)
    {
        if (value is null || !NamePattern().IsMatch(value))
        {
            throw new InvalidOperationException($"{name} must be 1 to 128 letters, digits, '-' or '_'.");
        }
    }

    private VaultTransitOptions SetAuth(VaultAuthMethod method)
    {
        if (Auth is not null)
        {
            throw new InvalidOperationException("An authentication method is already set.");
        }

        Auth = method;
        return this;
    }

    private static bool IsLoopback(Uri address) =>
        address.IsLoopback || (IPAddress.TryParse(address.Host.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip));

    private static bool LooksLikeToken(string text) =>
        text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal) ||
        text.StartsWith("hvs.", StringComparison.Ordinal) || text.StartsWith("hvb.", StringComparison.Ordinal) || text.StartsWith("hvr.", StringComparison.Ordinal) ||
        TokenShape().IsMatch(text);

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}(/[A-Za-z0-9_-]{1,64}){0,3}$")]
    private static partial Regex MountPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,128}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}(/[A-Za-z0-9._-]{1,128}){0,7}/?$")]
    private static partial Regex NamespacePattern();

    // The shape of a legacy Vault token ("s." and 24 letters or digits); a file path has a separator or an extension.
    [GeneratedRegex(@"^s\.[A-Za-z0-9]{20,}$")]
    private static partial Regex TokenShape();
}

internal abstract record VaultAuthMethod
{
    internal sealed record TokenFile(string Path) : VaultAuthMethod;

    internal sealed record Source(Func<CancellationToken, ValueTask<string>> Get) : VaultAuthMethod;

    internal sealed record Kubernetes(string Role, string JwtPath, string AuthMount) : VaultAuthMethod;

    internal sealed record AppRole(string RoleId, Func<CancellationToken, ValueTask<string>> SecretId, string AuthMount) : VaultAuthMethod;
}
