using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>The regions of the embedded dashboard page do what the page says.</summary>
public sealed class EmbeddedApplicationExamplesTests
{
    [Fact]
    public async Task The_policy_the_dashboard_requires_is_defined_and_wants_a_signed_in_person()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        _ = EmbeddedApplicationExamples.AddKairosApplication(services, "Host=localhost;Database=x", new Kairos.Worker.EnvelopePayloadProtector(new Kairos.Cloud.SelfHosted.LocalKeyProvider(new Dictionary<string, byte[]> { ["k"] = new byte[32] }, "k")));
        using ServiceProvider provider = services.BuildServiceProvider();

        AuthorizationPolicy? policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync("KairosDashboard");

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public void Only_the_named_proxy_is_believed_and_the_scheme_and_host_are_forwarded()
    {
        var services = new ServiceCollection();
        EmbeddedApplicationExamples.TrustTheProxy(services, IPAddress.Parse("10.1.2.3"));
        using ServiceProvider provider = services.BuildServiceProvider();

        ForwardedHeadersOptions options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal([IPAddress.Parse("10.1.2.3")], options.KnownProxies.Where(p => p.ToString() == "10.1.2.3").ToArray());
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
    }
}
