using Kairos.Agents;
using Kairos.Testing;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>More places to die: parallel tool calls, a rejected approval, a budget that pauses, and a conversation that hands over to a fresh run. Each is killed at every store operation, before and after the store applied it.</summary>
public sealed class CrashMatrixTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    /// <summary>Kills the worker at every store operation of <paramref name="drive"/>; the outcome string must equal the fault-free one, and <paramref name="check"/> holds after each.</summary>
    private static async Task MatrixAsync(Func<AfHarness> build, Func<AfHarness, Task<string>> drive, Func<AfHarness, string, Task>? check = null, int minimumOperations = 20)
    {
        AfHarness baseline = build();
        baseline.Faults!.FaultsEnabled = false;
        string expected = await drive(baseline);
        long operations = baseline.Faults.OperationCount;
        Assert.True(operations >= minimumOperations, $"The reference run did {operations} store operations, too few to be a meaningful crash matrix.");

        int crashes = 0;
        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                AfHarness h = build();
                h.Faults!.FaultsEnabled = false;
                long at = k;
                FaultKind crash = kind;
                h.Faults.ForcedFault = index => index == at ? crash : FaultKind.None;

                string actual = await drive(h);
                crashes += h.Faults.OperationCount > at ? 1 : 0;

                string where = $"crash {kind} at store operation {k}";
                Assert.True(expected == actual, $"{where}: expected '{expected}' but the outcome was '{actual}'.");
                if (check is not null)
                {
                    await check(h, where);
                }
            }
        }

        Assert.True(crashes > 0, "No crash was actually injected.");
        Console.Error.WriteLine($"[crash-matrix] {operations} store operations, {crashes} crashes injected and survived");
    }

    [Fact]
    public async Task Parallel_tool_calls_survive_a_crash_at_any_store_operation_and_each_runs_once_in_the_world()
    {
        var worlds = new List<(FakeExternalSystem External, ScriptedChatClient Model)>();
        AfHarness Build()
        {
            var external = new FakeExternalSystem();
            var model = new ScriptedChatClient(messages => messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any())
                ? new ChatMessage(ChatRole.Assistant, "both done")
                : new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c-a", "tool_a", AfHarness.Args("""{"q":"a"}""")), new FunctionCallContent("c-b", "tool_b", AfHarness.Args("""{"q":"b"}"""))]));
            AIFunction Tool(string name) => AIFunctionFactory.Create((string q) =>
            {
                external.Apply(AgentFrameworkToolContext.Current!.IdempotencyKey);
                return "result-" + name;
            }, name, name);
            worlds.Add((external, model));
            return new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "agent", Tool("tool_a"), Tool("tool_b")), r => r.ReadOnly("tool_a", "tool_b")), faultInjection: true);
        }

        await MatrixAsync(
            Build,
            async h =>
            {
                (AgentResponse? reply, _) = await h.AskAsync("agent", "s", "go");
                return AfHarness.Text(reply);
            },
            (h, where) =>
            {
                (FakeExternalSystem external, ScriptedChatClient model) = worlds[^1];
                Assert.True(external.Effects.Count == 2, $"{where}: {external.Effects.Count} effects.");
                Assert.True(model.CallCount is >= 2 and <= 3, $"{where}: {model.CallCount} model calls for 2 steps.");
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task A_rejected_approval_survives_a_crash_at_any_store_operation_and_the_tool_never_runs()
    {
        var worlds = new List<int[]>();
        AfHarness Build()
        {
            int[] runs = [0];
            worlds.Add(runs);
            ScriptedChatClient model = AfHarness.LookupRefundAnswer("not refunded");
            return new AfHarness(af => af.AddAgent(AfHarness.SupportAgent(model, refund: _ => { runs[0]++; return "refunded"; }), r => r.ReadOnly("lookup_order")), faultInjection: true);
        }

        await MatrixAsync(
            Build,
            async h =>
            {
                (_, DurableTurn turn) = await h.AskAsync("support", "s", "refund A-1042");
                RunKey run = await h.RunOfAsync("support", "s");
                string id = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;
                await AfHarness.Retry(async () => await h.Approvals.RejectAsync(run, id, Alice, "no").AsTask());
                await h.DriveAsync();
                return AfHarness.Text(await h.ReadAsync("support", turn));
            },
            (h, where) =>
            {
                Assert.True(worlds[^1][0] == 0, $"{where}: the rejected refund ran.");
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task A_budget_that_pauses_survives_a_crash_at_any_store_operation()
    {
        var calls = new List<CountingGuard>();
        AfHarness Build()
        {
            bool refused = false;
            var guard = new CountingGuard
            {
                Decide = info =>
                {
                    if (info.IdempotencyKey.EndsWith(":1", StringComparison.Ordinal) && info.AllowanceApprovalId is null && !refused)
                    {
                        refused = true;
                        return ModelCallPermit.Refuse("daily-tokens", "pause");
                    }

                    return ModelCallPermit.Allow("r-" + info.IdempotencyKey);
                },
            };
            calls.Add(guard);
            return new AfHarness(
                af => af.ConfigureAgents(b => b.UseModelCallGuard(guard)).AddAgent(AfHarness.AgentWith(AfHarness.Echo("answer"), "a"), r => r.Model("gpt-test", 100)),
                faultInjection: true);
        }

        await MatrixAsync(
            Build,
            async h =>
            {
                (_, DurableTurn turn) = await h.AskAsync("a", "s", "go");
                RunKey run = await h.RunOfAsync("a", "s");
                IReadOnlyList<PendingApproval> pending = AgentHistory.PendingApprovals(await h.HistoryAsync(run));
                if (pending.Count == 1)
                {
                    await AfHarness.Retry(async () => await h.Approvals.ApproveAsync(run, pending[0].ApprovalId, Alice).AsTask());
                    await h.DriveAsync();
                }

                return AfHarness.Text(await h.ReadAsync("a", turn));
            },
            minimumOperations: 15);
    }

    [Fact]
    public async Task A_conversation_that_hands_over_to_a_fresh_run_survives_a_crash_at_any_store_operation()
    {
        var models = new List<ScriptedChatClient>();
        AfHarness Build()
        {
            var model = new ScriptedChatClient(messages => new ChatMessage(ChatRole.Assistant, $"answer {messages.Count(m => m.Role == ChatRole.User)}"));
            models.Add(model);
            return new AfHarness(af => af.Configure(o => o.ContinueAfterEvents = 8).AddAgent(AfHarness.AgentWith(model, "chat")), faultInjection: true);
        }

        await MatrixAsync(
            Build,
            async h =>
            {
                var answers = new List<string>();
                for (int i = 1; i <= 4; i++)
                {
                    (AgentResponse? reply, _) = await h.AskAsync("chat", "s", $"message {i}");
                    answers.Add(AfHarness.Text(reply));
                }

                return string.Join('|', answers);
            },
            (h, where) =>
            {
                ScriptedChatClient model = models[^1];
                Assert.True(model.CallCount is >= 4 and <= 5, $"{where}: {model.CallCount} model calls for 4 messages.");
                return Task.CompletedTask;
            },
            minimumOperations: 40);
    }

    // ---- random faults -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    [InlineData(4UL)]
    [InlineData(5UL)]
    [InlineData(6UL)]
    [InlineData(7UL)]
    [InlineData(8UL)]
    public async Task Under_random_crashes_delays_and_duplicate_deliveries_every_step_still_runs_once_in_the_world(ulong seed)
    {
        var external = new FakeExternalSystem();
        ScriptedChatClient model = AfHarness.LookupRefundAnswer();
        var h = new AfHarness(
            af => af.AddAgent(
                AfHarness.SupportAgent(model, refund: _ => { external.Apply(AgentFrameworkToolContext.Current!.IdempotencyKey); return "refunded"; }),
                r => r.ReadOnly("lookup_order")),
            faultInjection: true, seed: seed);
        h.Faults!.FaultsEnabled = true;

        DurableAIAgent support = h.Agents.Get("support");
        DurableAgentSession session = support.Session("s");
        DurableTurn turn = await AfHarness.Retry(async () => await support.SendAsync([new ChatMessage(ChatRole.User, "refund A-1042")], session, "m-1").AsTask());
        await h.DriveAsync();
        RunKey run = await h.RunOfAsync("support", "s");
        string approval = AgentHistory.PendingApprovals(await h.HistoryAsync(run)).Single().ApprovalId;
        await AfHarness.Retry(async () => await h.Approvals.ApproveAsync(run, approval, Alice).AsTask());
        await h.DriveAsync();

        AgentResponse? reply = await h.ReadAsync("support", turn);
        Assert.Equal("Refund issued.", AfHarness.Text(reply));
        Assert.Single(external.Effects);
        Assert.True(model.CallCount >= 3 && model.CallCount <= 3 + h.Stats.Crashes + h.Stats.LeaseExpirations + 2, $"seed {seed}: {model.CallCount} model calls with {h.Stats.Crashes} crashes.");
        HashChain.Verify(await h.HistoryAsync(run));
    }
}
