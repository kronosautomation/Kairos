using System.Net.Http.Json;

namespace Kairos.Persistence.Tests.Faults;

/// <summary>Minimal client for the Toxiproxy HTTP API (github.com/Shopify/toxiproxy, HTTP API section of the README).</summary>
internal sealed class ToxiproxyClient(Uri api) : IDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = api, Timeout = TimeSpan.FromSeconds(30) };

    public Task CreateProxyAsync(string name, string listen, string upstream) =>
        SendAsync(HttpMethod.Post, "/proxies", new { name, listen, upstream, enabled = true });

    /// <summary>Adds a toxic. <paramref name="stream"/> is "upstream" (client to server) or "downstream" (server to client).</summary>
    public Task AddToxicAsync(string proxy, string name, string type, string stream, object attributes, double toxicity = 1.0) =>
        SendAsync(HttpMethod.Post, $"/proxies/{proxy}/toxics", new { name, type, stream, toxicity, attributes });

    public Task RemoveToxicAsync(string proxy, string name) => SendAsync(HttpMethod.Delete, $"/proxies/{proxy}/toxics/{name}", null);

    /// <summary>A disabled proxy refuses and drops connections, as a partition or a dead host would.</summary>
    public Task SetEnabledAsync(string proxy, bool enabled) => SendAsync(HttpMethod.Post, $"/proxies/{proxy}", new { enabled });

    /// <summary>Points the proxy at another upstream (a failover flip of a virtual IP or DNS name).</summary>
    public Task SetUpstreamAsync(string proxy, string upstream) => SendAsync(HttpMethod.Post, $"/proxies/{proxy}", new { upstream });

    /// <summary>Removes every toxic and re-enables every proxy.</summary>
    public Task ResetAsync() => SendAsync(HttpMethod.Post, "/reset", null);

    public void Dispose() => _http.Dispose();

    private async Task SendAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using HttpResponseMessage response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Toxiproxy {method} {path} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        }
    }
}
