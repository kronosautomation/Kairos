using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using Kairos.Agents;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kairos.Samples.ApprovalsPage;

/// <summary>Where the page looks for approvals: one tenant and namespace.</summary>
public sealed record ApprovalsPageOptions(TenantScope Scope)
{
    /// <summary>The most open runs the page reads when it builds the list. Older runs than that are not shown.</summary>
    public int MaxRuns { get; init; } = 200;
}

/// <summary>
/// A page that lists the approvals nobody has decided yet and lets a signed-in person approve or reject them. Your application signs the person in; the page decides nothing about who they are.
/// </summary>
public static class ApprovalsEndpoints
{
    public const string ReadPolicy = "approvals-read";

    public const string DecidePolicy = "approvals-decide";

    /// <summary>A person with this role claim may see the list.</summary>
    public const string ReaderRole = "approvals-reader";

    /// <summary>A person with this role claim may see the list and decide.</summary>
    public const string ApproverRole = "approvals-approver";

    private const string StyleSheet = "body{font-family:system-ui,sans-serif;margin:2rem auto;max-width:60rem;padding:0 1rem}table{border-collapse:collapse;width:100%}"
        + "th,td{border-bottom:1px solid #ccc;padding:.5rem;text-align:left;vertical-align:top}form{display:inline-block;margin:.25rem .25rem 0 0}.note{background:#eef;padding:.5rem}";

    #region register
    /// <summary>
    /// Registers what the page needs. Your application has already added Kairos, an <see cref="AgentClient"/> built with an evidence signer, and authentication. The two policies are the only
    /// authorization the page applies: one to see, one to decide. Map them to your own roles or claims here if you like.
    /// </summary>
    public static IServiceCollection AddApprovalsPage(this IServiceCollection services, ApprovalsPageOptions options)
    {
        services.AddSingleton(options);
        services.AddAntiforgery(a =>
        {
            a.HeaderName = "X-CSRF-TOKEN";                 // for a caller that can set a header; a plain form sends the token in a hidden field
            a.Cookie.Name = "kairos-approvals-csrf";
            a.Cookie.HttpOnly = true;
            a.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddAuthorizationBuilder()
            .AddPolicy(ReadPolicy, p => p.RequireAuthenticatedUser().RequireRole(ReaderRole, ApproverRole))
            .AddPolicy(DecidePolicy, p => p.RequireAuthenticatedUser().RequireRole(ApproverRole));
        return services;
    }
    #endregion

    #region map
    public static IEndpointRouteBuilder MapApprovalsPage(this IEndpointRouteBuilder app, string path = "/approvals")
    {
        RouteGroupBuilder group = app.MapGroup(path);
        group.AddEndpointFilter(async (context, next) =>
        {
            SetSecurityHeaders(context.HttpContext.Response);
            return await next(context);
        });

        group.MapGet("/page.css", () => Results.Text(StyleSheet, "text/css")).AllowAnonymous();

        group.MapGet("", ListAsync).RequireAuthorization(ReadPolicy);
        group.MapPost("/{runId:guid}/{approvalId}/approve", (HttpContext c, Guid runId, string approvalId) => DecideAsync(c, runId, approvalId, approved: true))
            .RequireAuthorization(DecidePolicy);
        group.MapPost("/{runId:guid}/{approvalId}/reject", (HttpContext c, Guid runId, string approvalId) => DecideAsync(c, runId, approvalId, approved: false))
            .RequireAuthorization(DecidePolicy);
        return app;
    }
    #endregion

    // No inline script, no inline style, nothing from another origin: the page works under a policy that allows none of them.
    private static void SetSecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "no-store";
    }

    private static async Task<IResult> ListAsync(HttpContext http, ApprovalsPageOptions options, IKairosStore store, KairosClient client, IAntiforgery antiforgery, CancellationToken cancellationToken)
    {
        IReadOnlyList<RunSummary> runs = await store.ListRunsAsync(options.Scope, new RunFilter { State = RunStateFilter.Open }, options.MaxRuns, cancellationToken);
        var rows = new List<(Guid RunId, PendingApproval Approval)>();
        foreach (RunSummary run in runs)
        {
            IReadOnlyList<HistoryEvent> history = await client.GetOpenedHistoryAsync(run.Run, cancellationToken);
            rows.AddRange(AgentHistory.PendingApprovals(history).Select(a => (run.Run.RunId, a)));
        }

        string token = antiforgery.GetAndStoreTokens(http).RequestToken!;
        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.WriteAsync(Render(rows, token, http.Request.Query["done"].ToString()), cancellationToken);
        return Results.Empty;
    }

    #region decide
    private static async Task<IResult> DecideAsync(HttpContext http, Guid runId, string approvalId, bool approved)
    {
        // A page that changes something must not be driven by another site: the token must come back in the form (or the header), and a browser that says the request is cross-site is refused.
        if (http.Request.Headers.TryGetValue("Sec-Fetch-Site", out Microsoft.Extensions.Primitives.StringValues site) && site != "same-origin")
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest("The form has expired. Reload the page and try again.");
        }

        // Who decides is whoever your application signed in: never a field of the form. The roles are the ones the sign-in gave them.
        string? subject = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        IFormCollection form = await http.Request.ReadFormAsync(http.RequestAborted);
        string? comment = form["comment"].ToString() is { Length: > 0 } c ? c[..Math.Min(c.Length, 500)] : null;
        var approver = new ApproverIdentity(subject, http.User.FindFirstValue(ClaimTypes.Name), [.. http.User.FindAll(ClaimTypes.Role).Select(r => r.Value)]);

        // The client signs the decision for that person with the evidence signer. The workers check the signature with the public key before the run goes on.
        ApprovalsPageOptions options = http.RequestServices.GetRequiredService<ApprovalsPageOptions>();
        var run = new RunKey(options.Scope.TenantId, options.Scope.NamespaceId, runId);
        AgentClient agents = http.RequestServices.GetRequiredService<AgentClient>();
        bool sent = approved
            ? await agents.ApproveAsync(run, approvalId, approver, comment, http.RequestAborted)
            : await agents.RejectAsync(run, approvalId, approver, comment, http.RequestAborted);

        return Results.Redirect("/approvals?done=" + (!sent ? "closed" : approved ? "approved" : "rejected"), permanent: false, preserveMethod: false);
    }
    #endregion

    private static string Render(List<(Guid RunId, PendingApproval Approval)> rows, string token, string done)
    {
        // Every value that came from a run or a person goes through HtmlEncode. The message comes from a fixed list, never from the query itself.
        string message = done switch
        {
            "approved" or "rejected" => "Your decision was sent. It is checked when the run next runs; if it was not accepted, the approval stays in this list.",
            "closed" => "That run has ended, so nothing was sent.",
            _ => "",
        };
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Approvals</title>"
            + "<link rel=\"stylesheet\" href=\"/approvals/page.css\"></head><body><h1>Waiting for a decision</h1>");
        if (message.Length > 0)
        {
            html.Append("<p class=\"note\">").Append(WebUtility.HtmlEncode(message)).Append("</p>");
        }

        if (rows.Count == 0)
        {
            html.Append("<p>Nothing is waiting.</p>");
        }
        else
        {
            html.Append("<table><thead><tr><th>Action</th><th>Details</th><th>Requested</th><th>Who may decide</th><th>Decide</th></tr></thead><tbody>");
            foreach ((Guid runId, PendingApproval a) in rows)
            {
                string who = a.Requirement.Roles is { Count: > 0 } r ? "roles: " + string.Join(", ", r)
                    : a.Requirement.Approvers is { Count: > 0 } p ? "people: " + string.Join(", ", p) : "any signed-in approver";
                string target = $"/approvals/{runId:D}/{Uri.EscapeDataString(a.ApprovalId)}";
                html.Append("<tr><td>").Append(WebUtility.HtmlEncode(a.Action)).Append("</td><td>").Append(WebUtility.HtmlEncode(a.Details ?? "")).Append("</td><td>")
                    .Append(WebUtility.HtmlEncode(a.RequestedAt.ToString("u", CultureInfo.InvariantCulture))).Append("</td><td>").Append(WebUtility.HtmlEncode(who)).Append("</td><td>");
                foreach ((string verb, string label) in new[] { ("approve", "Approve"), ("reject", "Reject") })
                {
                    html.Append("<form method=\"post\" action=\"").Append(WebUtility.HtmlEncode(target)).Append('/').Append(verb).Append("\">")
                        .Append("<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"").Append(WebUtility.HtmlEncode(token)).Append("\">")
                        .Append("<input type=\"text\" name=\"comment\" maxlength=\"500\" aria-label=\"Comment\" placeholder=\"Comment\">")
                        .Append("<button type=\"submit\">").Append(label).Append("</button></form>");
                }

                html.Append("</td></tr>");
            }

            html.Append("</tbody></table>");
        }

        return html.Append("</body></html>").ToString();
    }
}
