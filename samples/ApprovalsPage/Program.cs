using System.Security.Claims;
using Kairos;
using Kairos.Agents;
using Kairos.Samples.ApprovalsPage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

#region wire-up
// The key that signs approval decisions. In production this is a Key Vault or KMS key behind an IApprovalEvidenceSigner, and only this
// application may ask it to sign. LocalApprovalEvidenceSigner holds a key in the process and is for development.
LocalApprovalEvidenceSigner signer = LocalApprovalEvidenceSigner.Create("approvals-dev-1");

// The workers check decisions with the public key only. A decision that is unsigned, signed by another key, expired, or for another run is ignored.
var verifier = new EsApproverVerifier(new Dictionary<string, byte[]> { [signer.KeyId] = signer.PublicKey });
AgentsRuntime agents = new AgentsBuilder().UseApproverVerifier(verifier).Build();

builder.Services.AddKairos(options => options
    .UsePostgresFromConfiguration(builder.Configuration)
    .AllowUnencryptedPayloads() // sample only: real conversations need UsePayloadProtector
    .AddActivities(agents.Activities));

builder.Services.AddSingleton(sp => new AgentClient(sp.GetRequiredService<KairosClient>(), TimeProvider.System, signer));
builder.Services.AddApprovalsPage(new ApprovalsPageOptions(KairosOptions.DefaultScope));
#endregion

// Your application's own sign-in goes here. This sample uses a cookie and, in development only, an address that signs you in as a named person.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
});

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapApprovalsPage();

if (app.Environment.IsDevelopment())
{
    // Development only: /dev/sign-in?user=dana&role=approvals-approver&role=finance
    app.MapGet("/dev/sign-in", async (HttpContext http, string user, string[] role) =>
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user:" + user), new Claim(ClaimTypes.Name, user), .. role.Select(r => new Claim(ClaimTypes.Role, r))],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await http.SignInAsync(new ClaimsPrincipal(identity));
        return Results.Redirect("/approvals");
    });
}

await app.RunAsync();
