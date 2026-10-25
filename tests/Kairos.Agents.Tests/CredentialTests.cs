using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.Tests;

public sealed class CredentialTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private static readonly DateTimeOffset T0 = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static ToolCredentialRequest Request(string tool = "refunds", string[]? scopes = null, TimeSpan? life = null) =>
        new(new RunKey(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), tool, scopes ?? [$"{tool}:read"], life ?? TimeSpan.FromMinutes(5), "user:alice");

    [Fact]
    public async Task A_token_verifies_for_its_scope_run_and_tool_and_for_nothing_else()
    {
        var issuer = new HmacToolCredentialIssuer(Key);
        ToolCredentialRequest request = Request("refunds", ["refunds:read"]);
        ToolCredential credential = await issuer.IssueAsync(request, T0, CancellationToken.None);

        VerifiedToolCredential? ok = issuer.Verify(credential.Token, "refunds:read", T0.AddMinutes(1));
        Assert.NotNull(ok);
        Assert.Equal(request.Run.RunId, ok.RunId);
        Assert.Equal("refunds", ok.Tool);
        Assert.Equal("user:alice", ok.OnBehalfOf);

        Assert.Null(issuer.Verify(credential.Token, "refunds:write", T0.AddMinutes(1))); // never granted
    }

    [Fact]
    public async Task A_token_expires()
    {
        var issuer = new HmacToolCredentialIssuer(Key);
        ToolCredential credential = await issuer.IssueAsync(Request(life: TimeSpan.FromMinutes(5)), T0, CancellationToken.None);

        Assert.NotNull(issuer.Verify(credential.Token, "refunds:read", T0.AddMinutes(4)));
        Assert.Null(issuer.Verify(credential.Token, "refunds:read", T0.AddMinutes(5)));
        Assert.Null(issuer.Verify(credential.Token, "refunds:read", T0.AddHours(1)));
    }

    [Fact]
    public async Task A_forged_altered_or_foreign_token_is_refused()
    {
        var issuer = new HmacToolCredentialIssuer(Key);
        var other = new HmacToolCredentialIssuer(RandomNumberGenerator.GetBytes(32));
        ToolCredential credential = await issuer.IssueAsync(Request(), T0, CancellationToken.None);

        Assert.Null(other.Verify(credential.Token, "refunds:read", T0)); // signed with another key (a rotated key revokes all)
        string flipped = credential.Token[..^2] + (credential.Token[^2] == 'A' ? 'B' : 'A') + credential.Token[^1];
        Assert.Null(issuer.Verify(flipped, "refunds:read", T0));
        foreach (string junk in new[] { "", "ktc_", "ktc_.", "ktc_a.b", "nope", new string('x', 5000) })
        {
            Assert.Null(issuer.Verify(junk, "refunds:read", T0));
        }

        Assert.Null(issuer.Verify(null, "refunds:read", T0));
    }

    [Fact]
    public async Task Lifetimes_are_capped_and_keys_must_be_strong()
    {
        var issuer = new HmacToolCredentialIssuer(Key);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await issuer.IssueAsync(Request(life: TimeSpan.FromHours(1)), T0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await issuer.IssueAsync(Request(life: TimeSpan.Zero), T0, CancellationToken.None));
        Assert.Throws<ArgumentException>(() => new HmacToolCredentialIssuer(new byte[16]));
    }

    [Fact]
    public async Task Printing_a_credential_does_not_reveal_the_token()
    {
        var issuer = new HmacToolCredentialIssuer(Key);
        ToolCredential credential = await issuer.IssueAsync(Request(), T0, CancellationToken.None);

        Assert.DoesNotContain(credential.Token, credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(credential.Token[10..30], $"{credential}", StringComparison.Ordinal);
    }

    // ---- through a run --------------------------------------------------------------------------------------

    private static (AgentHarness H, List<ToolCredential> Seen) Run(ToolAccess access, string output = "ok", Action<BaselineToolPolicy>? policy = null)
    {
        var seen = new List<ToolCredential>();
        var issuer = new HmacToolCredentialIssuer(Key);
        AgentHarness h = new(b =>
        {
            b.AddModel("m", AgentHarness.Echo())
                .UseCredentials(issuer)
                .ConfigureBaselinePolicy(policy ?? (p => p.Allow("tool")))
                .AddTool(new AgentTool("tool", "x", """{"type":"object"}""", access, (i, _) =>
                {
                    seen.Add(i.Credential!);
                    return ValueTask.FromResult(output.Replace("{token}", i.Credential!.Token, StringComparison.Ordinal));
                }));
        });
        h.Workflow("call", async (ctx, _) =>
        {
            ctx.OnBehalfOf = "user:alice";
            ChatMessage reply = await ctx.InvokeToolAsync(new FunctionCallContent("c", "tool", null));
            return Encoding.UTF8.GetBytes(((FunctionResultContent)reply.Contents[0]).Result?.ToString() ?? "");
        });
        return (h, seen);
    }

    [Fact]
    public async Task A_read_only_tool_gets_a_credential_that_can_only_read()
    {
        (AgentHarness h, List<ToolCredential> seen) = Run(ToolAccess.ReadOnly);
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);

        ToolCredential credential = Assert.Single(seen);
        Assert.Equal(["tool:read"], credential.Scopes);
        var verifier = new HmacToolCredentialIssuer(Key);
        Assert.NotNull(verifier.Verify(credential.Token, "tool:read", h.Time.GetUtcNow()));
        Assert.Null(verifier.Verify(credential.Token, "tool:write", h.Time.GetUtcNow())); // whatever the tool's code tries
    }

    [Fact]
    public async Task A_write_tool_gets_write_scope_only_because_a_policy_or_person_allowed_it()
    {
        (AgentHarness h, List<ToolCredential> seen) = Run(ToolAccess.SideEffecting);
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);

        Assert.Equal(["tool:read", "tool:write"], Assert.Single(seen).Scopes);
    }

    [Fact]
    public async Task The_credential_is_bound_to_this_run_and_the_user_it_acts_for()
    {
        (AgentHarness h, List<ToolCredential> seen) = Run(ToolAccess.ReadOnly);
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);

        VerifiedToolCredential claims = new HmacToolCredentialIssuer(Key).Verify(seen[0].Token, "tool:read", h.Time.GetUtcNow())!;
        Assert.Equal(run.RunId, claims.RunId);
        Assert.Equal(run.NamespaceId, claims.NamespaceId);
        Assert.Equal("user:alice", claims.OnBehalfOf);
        Assert.True(claims.ExpiresAt - h.Time.GetUtcNow() <= TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_token_is_never_written_to_history_even_if_a_tool_returns_it()
    {
        (AgentHarness h, List<ToolCredential> seen) = Run(ToolAccess.ReadOnly, output: "here is my token: {token}");
        RunKey run = await h.StartAsync("call");
        Assert.Equal(RunState.Completed, await h.RunAsync(run));

        string token = seen[0].Token;
        foreach (HistoryEvent e in await h.HistoryAsync(run))
        {
            string text = e switch
            {
                ActivityScheduled s => AgentHarness.Text(s.Input),
                ActivityCompleted c => AgentHarness.Text(c.Result),
                WorkflowCompleted w => AgentHarness.Text(w.Result),
                ActivityFailed f => f.Message,
                _ => "",
            };
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
            Assert.DoesNotContain("ktc_", text, StringComparison.Ordinal);
        }

        Assert.Contains("[credential removed]", await h.ResultTextAsync(run), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_attempt_gets_a_fresh_token_so_a_leaked_one_dies_with_its_expiry()
    {
        int attempts = 0;
        var seen = new List<string>();
        var issuer = new HmacToolCredentialIssuer(Key);
        AgentHarness h = new(b => b.AddModel("m", AgentHarness.Echo()).UseCredentials(issuer).AddTool(
            new AgentTool("tool", "x", """{"type":"object"}""", ToolAccess.ReadOnly, (i, _) =>
            {
                seen.Add(i.Credential!.Token);
                if (++attempts < 3)
                {
                    throw new InvalidOperationException("transient");
                }

                return ValueTask.FromResult("ok");
            })));
        h.Workflow("call", async (ctx, _) =>
        {
            await ctx.InvokeToolAsync(new FunctionCallContent("c", "tool", null));
            return [];
        });
        RunKey run = await h.StartAsync("call");
        await h.RunAsync(run);

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
    }
}
