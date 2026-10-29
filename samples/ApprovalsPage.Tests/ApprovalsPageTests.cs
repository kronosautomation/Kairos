using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Kairos.Agents;
using Kairos.Samples.ApprovalsPage;
using Kairos.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kairos.Samples.ApprovalsPage.Tests;

/// <summary>A test sign-in: the header X-Test-User carries "subject|name|role,role". No header means nobody is signed in.</summary>
internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User", out Microsoft.Extensions.Primitives.StringValues header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string[] parts = header.ToString().Split('|');
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, parts[0]), new(ClaimTypes.Name, parts[1])];
        claims.AddRange(parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim(ClaimTypes.Role, r)));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new System.Security.Claims.ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
    }
}

#pragma warning disable CA1001 // Disposed by DisposeAsync, which xUnit calls.
public sealed class ApprovalsPageTests : IAsyncLifetime
{
    private static readonly TenantScope Scope = new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private const string Dana = "user:dana|Dana|approvals-approver,finance";
    private const string Reader = "user:rita|Rita|approvals-reader";
    private const string Nobody = "user:ned|Ned|"; // signed in, with no role that concerns approvals

    private readonly LocalApprovalEvidenceSigner _signer = LocalApprovalEvidenceSigner.Create("page-test-1");
    private readonly InMemoryKairosStore _store = new();
    private readonly VirtualTimeProvider _time = new();
    private readonly WorkflowRegistry _workflows = new();
    private readonly ActivityRegistry _activities = new();
    private WebApplication? _app;
    private HttpClient _http = null!;
    private WorkerRuntime _worker = null!;
    private KairosClient _client = null!;

    public async Task InitializeAsync()
    {
        // The workers verify with the public key of the signer the test names "the real one"; the page signs with whatever it is given.
        AgentsRuntime agents = new AgentsBuilder()
            .UseApproverVerifier(new EsApproverVerifier(new Dictionary<string, byte[]> { [_signer.KeyId] = _signer.PublicKey }))
            .Build();
        agents.RegisterInto(_activities);
        _client = new KairosClient(_store, _time);
        _worker = new WorkerRuntime(_store, Scope, _workflows, _activities, _time, new WorkerOptions { AutoHeartbeat = false });
        _workflows.Register("refund", KairosClient.DefaultBuildId, agents.Workflow(RefundAsync));
        await StartPageAsync(_signer);
    }

    private async Task StartPageAsync(IApprovalEvidenceSigner pageSigner)
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton<IKairosStore>(_store);
        builder.Services.AddSingleton(_client);
        builder.Services.AddSingleton(new AgentClient(_client, _time, pageSigner));
        builder.Services.AddApprovalsPage(new ApprovalsPageOptions(Scope));
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapApprovalsPage();
        await app.StartAsync();
        _app = app;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync() => await StopPageAsync();

    private async Task StopPageAsync()
    {
        _http.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    /// <summary>The workflow under test: it asks finance to approve, and returns who decided and how.</summary>
    private static async Task<byte[]> RefundAsync(IAgentContext ctx, byte[] input)
    {
        ApprovalDecision decision = await ctx.RequestApprovalAsync(new ApprovalRequest
        {
            Action = Encoding.UTF8.GetString(input),
            Details = "Order 8841, 250 EUR",
            Requirement = new ApprovalRequirement(Roles: ["finance"]),
        });
        return Encoding.UTF8.GetBytes($"{decision.Outcome} by {decision.Approver?.Subject} ({decision.Comment})");
    }

    private async Task<RunKey> StartRefundAsync(string action = "Refund 250 EUR to order 8841")
    {
        StartRunResult started = await _client.StartAsync(Scope, "refund-" + Guid.NewGuid().ToString("N"), "refund", Encoding.UTF8.GetBytes(action));
        var run = new RunKey(Scope.TenantId, Scope.NamespaceId, started.RunId);
        await Driver.DriveAsync(_worker);
        return run;
    }

    private async Task<string?> ResultAsync(RunKey run)
    {
        await Driver.DriveAsync(_worker);
        IReadOnlyList<HistoryEvent> history = await _client.GetOpenedHistoryAsync(run);
        return history[^1] is WorkflowCompleted done ? Encoding.UTF8.GetString(done.Result) : null;
    }

    private async Task<int> PendingAsync(RunKey run) => AgentHistory.PendingApprovals(await _client.GetOpenedHistoryAsync(run)).Count;

    private async Task<HttpResponseMessage> GetAsync(string? user)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/approvals");
        if (user is not null)
        {
            request.Headers.Add("X-Test-User", user);
        }

        return await _http.SendAsync(request);
    }

    /// <summary>Loads the page as the user (which sets the anti-forgery cookie and shows the form), then posts the form of the first approval for <paramref name="verb"/>.</summary>
    private async Task<HttpResponseMessage> PostAsync(string user, RunKey run, string verb, Action<Dictionary<string, string>>? changeForm = null, Action<HttpRequestMessage>? changeRequest = null, bool sendToken = true, bool tokenInHeader = false)
    {
        string page = await (await GetAsync(user)).Content.ReadAsStringAsync();
        string token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        string approvalId = Regex.Match(page, $"/approvals/{run.RunId:D}/([^/\"]+)/{verb}").Groups[1].Value;

        var form = new Dictionary<string, string> { ["comment"] = "checked against the invoice" };
        if (sendToken && !tokenInHeader)
        {
            form["__RequestVerificationToken"] = token;
        }

        changeForm?.Invoke(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/approvals/{run.RunId:D}/{approvalId}/{verb}") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("X-Test-User", user);
        if (sendToken && tokenInHeader)
        {
            request.Headers.Add("X-CSRF-TOKEN", token);
        }

        changeRequest?.Invoke(request);
        return await _http.SendAsync(request);
    }

    [Fact]
    public async Task The_page_lists_the_pending_approval_and_sets_the_headers_a_strict_policy_needs()
    {
        await StartRefundAsync();

        HttpResponseMessage response = await GetAsync(Dana);
        string html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Refund 250 EUR to order 8841", html, StringComparison.Ordinal);
        Assert.Contains("roles: finance", html, StringComparison.Ordinal);
        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        // One document, no script, no inline style or event handler, nothing from another origin.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(new Regex(@"\son\w+\s*=", RegexOptions.IgnoreCase), html);
        Assert.DoesNotMatch(new Regex(@"(src|href|action)\s*=\s*""(https?:)?//", RegexOptions.IgnoreCase), html);
    }

    [Fact]
    public async Task What_a_run_wrote_is_shown_as_text_never_as_markup()
    {
        await StartRefundAsync("<script>alert('x')</script>");

        string html = await (await GetAsync(Dana)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_with_verified_evidence_lets_the_run_go_on_and_records_the_signed_in_person()
    {
        RunKey run = await StartRefundAsync();

        HttpResponseMessage response = await PostAsync(Dana, run, "approve");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("Approved by user:dana (checked against the invoice)", await ResultAsync(run));
    }

    [Fact]
    public async Task Rejecting_ends_the_approval_as_rejected()
    {
        RunKey run = await StartRefundAsync();

        await PostAsync(Dana, run, "reject");

        Assert.Equal("Rejected by user:dana (checked against the invoice)", await ResultAsync(run));
    }

    [Fact]
    public async Task The_person_is_the_one_signed_in_never_one_named_in_the_form()
    {
        RunKey run = await StartRefundAsync();

        await PostAsync(Dana, run, "approve", form =>
        {
            form["subject"] = "user:eve";
            form["Subject"] = "user:eve";
            form["roles"] = "finance";
        });

        Assert.Equal("Approved by user:dana (checked against the invoice)", await ResultAsync(run));
    }

    [Fact]
    public async Task A_post_without_the_anti_forgery_token_changes_nothing()
    {
        RunKey run = await StartRefundAsync();

        HttpResponseMessage response = await PostAsync(Dana, run, "approve", sendToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await ResultAsync(run));
        Assert.Equal(1, await PendingAsync(run));
    }

    [Fact]
    public async Task The_token_is_also_accepted_in_a_header_for_a_caller_that_can_set_one()
    {
        RunKey run = await StartRefundAsync();

        await PostAsync(Dana, run, "approve", tokenInHeader: true);

        Assert.Equal("Approved by user:dana (checked against the invoice)", await ResultAsync(run));
    }

    [Fact]
    public async Task A_post_the_browser_says_came_from_another_site_changes_nothing()
    {
        RunKey run = await StartRefundAsync();

        HttpResponseMessage response = await PostAsync(Dana, run, "approve", changeRequest: r => r.Headers.Add("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await ResultAsync(run));
    }

    [Fact]
    public async Task Nobody_signed_in_sees_nothing_and_decides_nothing()
    {
        RunKey run = await StartRefundAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(null)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/approvals/{run.RunId:D}/x/approve") { Content = new FormUrlEncodedContent(new Dictionary<string, string>()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(request)).StatusCode);
        Assert.Equal(1, await PendingAsync(run));
    }

    [Fact]
    public async Task A_signed_in_user_with_no_reader_role_can_neither_see_nor_decide()
    {
        RunKey run = await StartRefundAsync();
        string approvalId = await PendingIdAsync(run);

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(Nobody)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/approvals/{run.RunId:D}/{Uri.EscapeDataString(approvalId)}/approve")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>()),
        };
        request.Headers.Add("X-Test-User", Nobody);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(request)).StatusCode);
        Assert.Null(await ResultAsync(run));
    }

    [Fact]
    public async Task A_reader_sees_the_list_but_cannot_decide()
    {
        RunKey run = await StartRefundAsync();

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(Reader)).StatusCode);
        HttpResponseMessage response = await PostAsync(Reader, run, "approve");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await ResultAsync(run));
        Assert.Equal(1, await PendingAsync(run));
    }

    [Fact]
    public async Task An_approver_without_the_role_the_approval_asks_for_is_not_counted()
    {
        RunKey run = await StartRefundAsync();

        await PostAsync("user:otto|Otto|approvals-approver", run, "approve"); // may use the page, but is not in finance

        Assert.Null(await ResultAsync(run));
        Assert.Equal(1, await PendingAsync(run)); // the approval stays in the list
    }

    [Fact]
    public async Task A_decision_with_no_signature_is_ignored_by_the_workers()
    {
        RunKey run = await StartRefundAsync();
        var unsigned = new AgentClient(_client, _time, evidenceSigner: null);

        Assert.True(await unsigned.ApproveAsync(run, await PendingIdAsync(run), new ApproverIdentity("user:dana", "Dana", ["finance"])));

        Assert.Null(await ResultAsync(run));
        Assert.Equal(1, await PendingAsync(run));
    }

    [Fact]
    public async Task A_decision_signed_with_another_key_is_ignored_by_the_workers()
    {
        RunKey run = await StartRefundAsync();
        LocalApprovalEvidenceSigner forger = LocalApprovalEvidenceSigner.Create(_signer.KeyId); // same key id, different key

        await new AgentClient(_client, _time, forger).ApproveAsync(run, await PendingIdAsync(run), new ApproverIdentity("user:dana", "Dana", ["finance"]));

        Assert.Null(await ResultAsync(run));
        Assert.Equal(1, await PendingAsync(run));
    }

    [Fact]
    public async Task A_decision_with_expired_evidence_is_ignored_by_the_workers()
    {
        RunKey run = await StartRefundAsync();
        string approvalId = await PendingIdAsync(run);
        DateTimeOffset now = _time.GetUtcNow();
        string stale = await ApprovalEvidence.MintAsync(
            _signer, new ApprovalEvidenceClaims(run, approvalId, true, "user:dana", ["finance"], now.AddMinutes(-10), now.AddMinutes(-5), ApprovalEvidence.NewEvidenceId()));

        await new AgentClient(_client, _time).ApproveAsync(run, approvalId, new ApproverIdentity("user:dana", "Dana", ["finance"], Evidence: stale));

        Assert.Null(await ResultAsync(run));
        Assert.Equal(1, await PendingAsync(run));
    }

    [Fact]
    public async Task A_page_whose_signer_is_not_the_one_the_workers_trust_cannot_approve()
    {
        await StopPageAsync(); // start the page again, signing with a key the workers do not know
        await StartPageAsync(LocalApprovalEvidenceSigner.Create("page-test-1"));
        RunKey run = await StartRefundAsync();

        HttpResponseMessage response = await PostAsync(Dana, run, "approve");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // the page cannot tell; the decision was sent
        Assert.Null(await ResultAsync(run));
        Assert.Equal(1, await PendingAsync(run)); // and it is still listed, which is how the person finds out
    }


    private async Task<string> PendingIdAsync(RunKey run) => AgentHistory.PendingApprovals(await _client.GetOpenedHistoryAsync(run)).Single().ApprovalId;
}
