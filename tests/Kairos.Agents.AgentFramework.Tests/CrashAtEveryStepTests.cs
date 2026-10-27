using Kairos.Agents;
using Kairos.Testing;
using Kairos.Testing.Agents;
using Kairos.Testing.Simulation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// Layer 4 for the adapter. For each scenario: count the store operations of a fault-free run, then kill the worker at every single operation boundary (before the store applied the call, and after it applied but before the
/// caller heard), restart, and require the same outcome, a verified history, and that nothing recorded is ever done again: the model is called once per step, plus at most one repeat for the step that was in flight; a tool
/// runs once per call, plus at most one repeat; and the outside world sees each effect once (the tool passes the step's idempotency key on).
/// </summary>
public sealed class CrashAtEveryStepTests
{
    private static readonly ApproverIdentity Alice = new("user:alice", "Alice", ["finance"]);

    /// <summary>Everything the world outside Kairos saw in one run.</summary>
    private sealed class World
    {
        public int Lookups;
        public int Refunds;
        public readonly FakeExternalSystem External = new();
        public readonly List<string?> RefundKeys = [];
    }

    private static (AfHarness Harness, World World, ScriptedChatClient Model) Build(Action<AgentRegistration>? register = null)
    {
        var world = new World();
        ScriptedChatClient model = AfHarness.LookupRefundAnswer();
        var h = new AfHarness(
            af => af.AddAgent(
                AfHarness.SupportAgent(
                    model,
                    lookup: _ =>
                    {
                        Interlocked.Increment(ref world.Lookups);
                        return "shipped";
                    },
                    refund: _ =>
                    {
                        Interlocked.Increment(ref world.Refunds);
                        string key = AgentFrameworkToolContext.Current!.IdempotencyKey;
                        lock (world.RefundKeys)
                        {
                            world.RefundKeys.Add(key);
                        }

                        world.External.Apply(key); // the refund service de-duplicates on the key
                        return "refunded";
                    }),
                r =>
                {
                    r.ReadOnly("lookup_order");
                    register?.Invoke(r);
                }),
            faultInjection: true);
        h.Faults!.FaultsEnabled = false;
        return (h, world, model);
    }

    /// <summary>One conversation: a message, the lookup, a refund that waits for a person, the person approves, the answer.</summary>
    private static async Task<(RunKey Run, AgentResponse Reply)> DriveAsync(AfHarness h)
    {
        DurableAIAgent durable = h.Agents.Get("support");
        DurableAgentSession session = durable.Session("s1");
        string messageId = "message-1";
        DurableTurn turn = await AfHarness.Retry(async () => await durable.SendAsync([new ChatMessage(ChatRole.User, "refund order A-1042")], session, messageId).AsTask());
        await h.DriveAsync();

        RunKey run = await h.RunOfAsync("support", "s1");
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        PendingApproval pending = Assert.Single(AgentHistory.PendingApprovals(history));
        await AfHarness.Retry(async () => await h.Approvals.ApproveAsync(run, pending.ApprovalId, Alice).AsTask());
        await h.DriveAsync();

        AgentResponse? reply = await h.ReadAsync("support", turn);
        Assert.NotNull(reply);
        return (run, reply!);
    }

    [Fact]
    public async Task Surviving_a_crash_at_any_store_operation_changes_nothing_and_never_repeats_a_recorded_step()
    {
        (AfHarness baseline, World baseWorld, ScriptedChatClient baseModel) = Build();
        (RunKey baseRun, AgentResponse baseReply) = await DriveAsync(baseline);
        string expected = await SignatureAsync(baseline, baseRun, baseReply);
        long operations = baseline.Faults!.OperationCount;
        Assert.True(operations > 30, "The reference run did too little to be a meaningful crash matrix.");
        Assert.Equal((3, 1, 1), (baseModel.CallCount, baseWorld.Lookups, baseWorld.Refunds));

        int crashes = 0;
        for (long k = 0; k < operations; k++)
        {
            foreach (FaultKind kind in new[] { FaultKind.Before, FaultKind.After })
            {
                (AfHarness h, World world, ScriptedChatClient model) = Build();
                long at = k;
                FaultKind crashKind = kind;
                h.Faults!.ForcedFault = index => index == at ? crashKind : FaultKind.None;

                (RunKey run, AgentResponse reply) = await DriveAsync(h);
                crashes += h.Faults.OperationCount > at ? 1 : 0;

                string where = $"crash {kind} at store operation {k}";
                Assert.True(expected == await SignatureAsync(h, run, reply), $"{where}: outcome differs from the fault-free run.");
                HashChain.Verify(await h.HistoryAsync(run));

                // Nothing recorded is done again: at most one repeat for the one step that was in flight when the worker died.
                Assert.True(model.CallCount is >= 3 and <= 4, $"{where}: {model.CallCount} model calls for 3 steps.");
                Assert.True(world.Lookups is >= 1 and <= 2, $"{where}: the lookup ran {world.Lookups} times.");
                Assert.True(world.Refunds is >= 1 and <= 2, $"{where}: the refund ran {world.Refunds} times.");
                Assert.True(model.CallCount + world.Lookups + world.Refunds <= 5 + 1, $"{where}: more than one step was repeated after one crash.");

                // The outside world saw the refund once, whatever the tool did twice, and under the key of the step that is in history.
                Assert.Single(world.External.Effects);
                Assert.Single(world.RefundKeys.Distinct());
                IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
                string toolKey = world.RefundKeys[0]!;
                Assert.Contains(history.OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentActivityNames.Tool), a => $"{run.RunId:N}:{a.ActivityId}" == toolKey);

                // Every model call carried the key of a step that is in history, so a provider can de-duplicate a repeat.
                var stepKeys = history.OfType<ActivityScheduled>().Where(a => a.ActivityType == AgentFrameworkNames.Step).Select(a => $"{run.RunId:N}:{a.ActivityId}").ToHashSet(StringComparer.Ordinal);
                Assert.All(model.Keys, key => Assert.True(stepKeys.Contains(key!), $"{where}: a model call carried the key {key}"));
            }
        }

        Assert.True(crashes > 0, "No crash was actually injected.");
        Console.Error.WriteLine($"[crash-matrix] {operations} store operations, {crashes} crashes injected and survived");
    }

    private static async Task<string> SignatureAsync(AfHarness h, RunKey run, AgentResponse reply)
    {
        IReadOnlyList<HistoryEvent> history = await h.HistoryAsync(run);
        string steps = string.Join(',', history.OfType<ActivityScheduled>().Select(a => a.ActivityType));
        return $"{steps}|{reply.Text}|{reply.Usage?.TotalTokenCount}";
    }
}
