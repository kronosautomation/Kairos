using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace Kairos.Samples.Concepts;

/// <summary>The parts of an application that hosts Kairos and a dashboard which are plain ASP.NET Core and the Kairos engine. The page that quotes them is the dashboard page.</summary>
public static class EmbeddedApplicationExamples
{
    #region app-services
    // Your application signs people in however it already does (AddAuthentication(...) with Entra ID, OpenID Connect, cookies).
    // The dashboard requires an authorization policy that you define, so that you decide what "signed in" means.
    public static IServiceCollection AddKairosApplication(IServiceCollection services, string connectionString, IPayloadProtector protector)
    {
        services.AddAuthorization(options => options.AddPolicy("KairosDashboard", policy => policy.RequireAuthenticatedUser()));
        return services.AddKairos(options => options.UsePostgres(connectionString).UsePayloadProtector(protector));
    }
    #endregion

    #region behind-a-proxy
    // Behind a load balancer that ends TLS, tell ASP.NET Core which proxy to believe about the scheme and host people used.
    // The dashboard answers only over HTTPS and checks the origin of every change against that address.
    public static void TrustTheProxy(IServiceCollection services, IPAddress proxy) =>
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            options.KnownProxies.Add(proxy);
        });

    // First in the pipeline, before authentication.
    public static void UseTheForwardedAddress(WebApplication app) => app.UseForwardedHeaders();
    #endregion
}
