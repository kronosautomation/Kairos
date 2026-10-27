using System.Text.Json;
using System.Text.Json.Nodes;
using Kairos.Testing.Agents;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// What a session costs in history: a step refers to the session an earlier step recorded instead of carrying it, and a result records it whole only every few steps and as changes in between. The codec and the chain
/// that put the session back together are tested on their own; the protocol is tested through real conversations, including a worker that has never seen the session and a run handed over to a fresh one.
/// </summary>
public sealed class SessionSizeTests
{
    // ---- the codec -------------------------------------------------------------------------------------------------

    private static JsonNode Parse(string json) => SnapshotCodec.Canonical(JsonDocument.Parse(json).RootElement);

    private static readonly string Emoji = char.ConvertFromUtf32(0x1F600);
    private static readonly string LineSeparator = ((char)0x2028).ToString();

    [Fact]
    public void The_same_session_is_the_same_text_whatever_order_its_members_were_written_in()
    {
        JsonNode a = Parse("""{"b":1,"a":{"y":[1,2],"x":"t"},"$type":"s"}""");
        JsonNode b = Parse("""{"$type":"s","a":{"x":"t","y":[1,2]},"b":1}""");

        Assert.Equal(SnapshotCodec.Text(a), SnapshotCodec.Text(b));
        Assert.Equal(SnapshotCodec.Hash(SnapshotCodec.Text(a)), SnapshotCodec.Hash(SnapshotCodec.Text(b)));
        Assert.StartsWith("{\"$type\"", SnapshotCodec.Text(a), StringComparison.Ordinal); // a polymorphic type tag stays first, as the serializer needs it
    }

    [Fact]
    public void Canonical_text_survives_being_read_and_written_again_unchanged()
    {
        var value = new JsonObject
        {
            ["t"] = "quote \" slash \\ tab \t line " + LineSeparator + " emoji " + Emoji + " accent " + (char)0xE9 + " kanji " + (char)0x65E5,
            ["n"] = JsonNode.Parse("[1.0,2.50,-0,1e3]"),
            ["z"] = null,
        };
        JsonNode node = Parse(value.ToJsonString());
        string text = SnapshotCodec.Text(node);

        Assert.Equal(text, SnapshotCodec.Text(SnapshotCodec.Canonical(SnapshotCodec.Element(text))));
        Assert.Equal(value["t"]!.GetValue<string>(), node["t"]!.GetValue<string>());
    }

    private static JsonNode? Random(Random random, int depth)
    {
        int kind = random.Next(depth > 2 ? 4 : 7);
        return kind switch
        {
            0 => random.Next(5),
            1 => "text " + random.Next(4) + (random.Next(3) == 0 ? " " + (char)0xE9 + Emoji : ""),
            2 => random.Next(2) == 0,
            3 => null,
            4 or 5 => new JsonArray([.. Enumerable.Range(0, random.Next(5)).Select(_ => Random(random, depth + 1))]),
            _ => new JsonObject(Enumerable.Range(0, random.Next(4)).Select(_ => new KeyValuePair<string, JsonNode?>(new[] { "a", "b", "c", "$type", "d" }[random.Next(5)], Random(random, depth + 1))).DistinctBy(p => p.Key)),
        };
    }

    /// <summary>A value near <paramref name="before"/>: one member changed, one message appended, one thing removed.</summary>
    private static JsonNode? Near(Random random, JsonNode? before)
    {
        switch (before)
        {
            case JsonObject o when random.Next(5) > 0:
                var copy = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> m in o)
                {
                    if (random.Next(6) == 0)
                    {
                        continue;
                    }

                    copy[m.Key] = random.Next(2) == 0 ? Near(random, m.Value) : m.Value?.DeepClone();
                }

                if (random.Next(3) == 0)
                {
                    copy[new[] { "a", "b", "c", "d", "e" }[random.Next(5)]] = Random(random, 2);
                }

                return copy;
            case JsonArray a when random.Next(5) > 0:
                var items = new JsonArray();
                foreach (JsonNode? item in a)
                {
                    items.Add(random.Next(6) == 0 ? Near(random, item) : item?.DeepClone());
                }

                if (random.Next(2) == 0)
                {
                    items.Add(Random(random, 2));
                }

                if (items.Count > 0 && random.Next(5) == 0)
                {
                    items.RemoveAt(random.Next(items.Count));
                }

                if (items.Count > 0 && random.Next(4) == 0)
                {
                    items.RemoveAt(0); // a window slides
                }

                return items;
            default:
                return random.Next(3) == 0 ? before?.DeepClone() : Random(random, 1);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_change_applied_to_the_session_before_gives_the_session_after_for_any_two_sessions(int seed)
    {
        var random = new Random(seed);
        for (int i = 0; i < 400; i++)
        {
            JsonNode? before = Random(random, 0);
            JsonNode? after = i % 2 == 0 ? Near(random, before) : Random(random, 0);
            if (before is null || after is null)
            {
                continue;
            }

            JsonNode canonicalBefore = SnapshotCodec.Canonical(JsonDocument.Parse(before.ToJsonString()).RootElement);
            JsonNode canonicalAfter = SnapshotCodec.Canonical(JsonDocument.Parse(after.ToJsonString()).RootElement);
            string beforeText = SnapshotCodec.Text(canonicalBefore);
            JsonNode change = SnapshotCodec.Diff(canonicalBefore, canonicalAfter) ?? new JsonObject { ["e"] = 1 };

            JsonNode? applied = SnapshotCodec.Apply(canonicalBefore, change);

            Assert.Equal(SnapshotCodec.Text(canonicalAfter), SnapshotCodec.Text(applied!));
            Assert.Equal(beforeText, SnapshotCodec.Text(canonicalBefore)); // applying changed nothing it was given
        }
    }

    [Fact]
    public void A_conversation_that_grows_by_messages_is_a_change_of_only_the_new_messages()
    {
        JsonNode before = Parse("""{"state":{"messages":[{"m":1},{"m":2}]},"id":"s"}""");
        JsonNode after = Parse("""{"state":{"messages":[{"m":1},{"m":2},{"m":3}]},"id":"s"}""");

        JsonNode change = SnapshotCodec.Diff(before, after)!;

        Assert.Equal("""{"o":{"state":{"o":{"messages":{"k":2,"t":[{"m":3}]}}}}}""", change.ToJsonString());
    }

    [Fact]
    public void A_conversation_kept_to_a_window_is_a_change_of_what_dropped_out_and_what_came_in()
    {
        JsonNode before = Parse("""{"messages":[{"m":1},{"m":2},{"m":3},{"m":4},{"m":5}]}""");
        JsonNode after = Parse("""{"messages":[{"m":2},{"m":3},{"m":4},{"m":5},{"m":6}]}""");

        JsonNode change = SnapshotCodec.Diff(before, after)!;

        Assert.Equal("""{"o":{"messages":{"f":1,"k":4,"t":[{"m":6}]}}}""", change.ToJsonString());
        Assert.Equal(SnapshotCodec.Text(after), SnapshotCodec.Text(SnapshotCodec.Apply(before, change)!));
    }

    [Fact]
    public void A_change_that_does_not_fit_what_it_is_applied_to_is_refused()
    {
        JsonNode array = Parse("[1,2,3]");

        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(array, JsonNode.Parse("""{"k":9,"t":[]}""")!));
        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(array, JsonNode.Parse("""{"f":2,"k":2,"t":[]}""")!));
        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(array, JsonNode.Parse("""{"f":-1,"k":1,"t":[]}""")!));
        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(array, JsonNode.Parse("""{"o":{"a":{"s":1}}}""")!));
        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(array, JsonNode.Parse("[1]")!));
    }

    // ---- the chain -------------------------------------------------------------------------------------------------

    private static (JsonElement Session, string Hash) Session(string json)
    {
        string text = SnapshotCodec.Text(Parse(json));
        return (SnapshotCodec.Element(text), SnapshotCodec.Hash(text));
    }

    private static AfStepResult Whole(string json, string step)
    {
        (JsonElement session, string hash) = Session(json);
        return new AfStepResult { Snapshot = session, SnapshotHash = hash, StepId = step };
    }

    private static AfStepResult Change(string before, string after, string step, string? baseHash = null)
    {
        JsonNode change = SnapshotCodec.Diff(Parse(before), Parse(after))!;
        return new AfStepResult
        {
            Delta = SnapshotCodec.Element(change.ToJsonString()),
            BaseHash = baseHash ?? Session(before).Hash,
            SnapshotHash = Session(after).Hash,
            StepId = step,
        };
    }

    [Fact]
    public void The_chain_puts_the_session_together_from_a_whole_one_and_the_changes_since()
    {
        var chain = new SessionChain();
        chain.Record(Whole("""{"m":[1]}""", "s1"));
        chain.Record(Change("""{"m":[1]}""", """{"m":[1,2]}""", "s2"));
        chain.Record(Change("""{"m":[1,2]}""", """{"m":[1,2,3],"x":true}""", "s3"));

        Assert.Equal(("s3", 2), (chain.From, chain.StepsSinceFull));
        Assert.Equal(SnapshotCodec.Text(Parse("""{"m":[1,2,3],"x":true}""")), SnapshotCodec.Text(SnapshotCodec.Canonical(chain.Materialize())));

        chain.Record(Whole("""{"m":[9]}""", "s4"));
        Assert.Equal(0, chain.StepsSinceFull);
        Assert.Equal("""{"m":[9]}""", chain.Materialize().GetRawText());
    }

    [Fact]
    public void The_chain_refuses_a_whole_session_that_is_not_the_one_its_hash_names()
    {
        var chain = new SessionChain();
        AfStepResult lying = Whole("""{"m":[1]}""", "s1") with { SnapshotHash = Session("""{"m":[2]}""").Hash };

        Assert.Throws<SnapshotIntegrityException>(() => chain.Record(lying));
    }

    [Fact]
    public void The_chain_refuses_a_change_against_a_session_it_does_not_hold()
    {
        var chain = new SessionChain();
        chain.Record(Whole("""{"m":[1]}""", "s1"));

        Assert.Throws<SnapshotIntegrityException>(() => chain.Record(Change("""{"m":[7]}""", """{"m":[7,8]}""", "s2"))); // its base is another session
        Assert.Throws<SnapshotIntegrityException>(() => new SessionChain().Record(Change("""{"m":[1]}""", """{"m":[1,2]}""", "s2"))); // nothing to apply it to
    }

    [Fact]
    public void A_change_that_does_not_make_the_session_its_hash_names_is_found_when_the_session_is_put_together()
    {
        var chain = new SessionChain();
        chain.Record(Whole("""{"m":[1]}""", "s1"));
        AfStepResult wrong = Change("""{"m":[1]}""", """{"m":[1,2]}""", "s2") with { SnapshotHash = Session("""{"m":[1,3]}""").Hash };
        chain.Record(wrong); // a change is not checked until it is read: that is the point of recording only what changed

        Assert.Throws<SnapshotIntegrityException>(() => chain.Materialize());
    }

    // ---- the worker's cache ----------------------------------------------------------------------------------------

    [Fact]
    public void The_cache_is_bounded_by_bytes_and_drops_the_least_recently_used_session()
    {
        var cache = new SnapshotCache(maxBytes: 300); // 150 characters
        var (tenant, ns) = (Guid.NewGuid(), Guid.NewGuid());
        cache.Put(tenant, ns, "a", new string('a', 60));
        cache.Put(tenant, ns, "b", new string('b', 60));
        Assert.True(cache.TryGet(tenant, ns, "a", out _)); // a is now the more recent
        cache.Put(tenant, ns, "c", new string('c', 60));

        Assert.True(cache.TryGet(tenant, ns, "a", out _));
        Assert.False(cache.TryGet(tenant, ns, "b", out _));
        Assert.True(cache.TryGet(tenant, ns, "c", out _));
        Assert.True(cache.Bytes <= 300);
    }

    [Fact]
    public void A_session_larger_than_the_whole_cache_is_not_kept_and_not_an_error()
    {
        var cache = new SnapshotCache(maxBytes: 100);
        var (tenant, ns) = (Guid.NewGuid(), Guid.NewGuid());

        cache.Put(tenant, ns, "big", new string('x', 500));

        Assert.False(cache.TryGet(tenant, ns, "big", out _));
        Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public void A_session_is_found_only_by_the_tenant_and_namespace_that_put_it()
    {
        var cache = new SnapshotCache(maxBytes: 10_000);
        var (tenant, ns) = (Guid.NewGuid(), Guid.NewGuid());
        cache.Put(tenant, ns, "hash", "secret session");

        Assert.True(cache.TryGet(tenant, ns, "hash", out string found));
        Assert.Equal("secret session", found);
        Assert.False(cache.TryGet(Guid.NewGuid(), ns, "hash", out _)); // another tenant that knows the hash finds nothing
        Assert.False(cache.TryGet(tenant, Guid.NewGuid(), "hash", out _));
    }

    // ---- the protocol, through conversations -----------------------------------------------------------------------

    private static (AfStepRequest Request, AfStepResult? Result)[] Steps(IReadOnlyList<HistoryEvent> history)
    {
        var requests = new Dictionary<int, AfStepRequest>();
        var results = new Dictionary<int, AfStepResult>();
        var order = new List<int>();
        foreach (HistoryEvent e in history)
        {
            if (e is ActivityScheduled s && s.ActivityType == AgentFrameworkNames.Step)
            {
                requests[s.ActivityId] = AfCodec.Read(s.Input, AfJson.Default.AfStepRequest);
                order.Add(s.ActivityId);
            }
            else if (e is ActivityCompleted c && requests.ContainsKey(c.ActivityId))
            {
                results[c.ActivityId] = AfCodec.Read(c.Result, AfJson.Default.AfStepResult);
            }
        }

        return [.. order.Select(id => (requests[id], results.GetValueOrDefault(id)))];
    }

    private static AfHarness Support(Action<AgentFrameworkOptions>? options = null, int? continueAfter = null)
    {
        return new AfHarness(af =>
        {
            af.Configure(o =>
            {
                o.MaxSnapshotBytes = 16 * 1024 * 1024;
                o.ContinueAfterEvents = continueAfter;
                options?.Invoke(o);
            });
            af.AddAgent(MeasurementTests.SupportAgent(MeasurementTests.SupportModel()), r => r.ReadOnly("lookup_order", "lookup_customer"));
        });
    }

    [Fact]
    public async Task A_step_refers_to_the_session_an_earlier_step_recorded_and_does_not_carry_it()
    {
        AfHarness h = Support();
        for (int i = 1; i <= 3; i++)
        {
            await h.AskAsync("support", "s", $"question {i}");
        }

        var steps = Steps(await h.HistoryAsync(await h.RunOfAsync("support", "s")));

        Assert.Equal(9, steps.Length);
        Assert.All(steps, s => Assert.Equal(AfProtocol.Referenced, s.Request.Protocol));
        Assert.Null(steps[0].Request.SnapshotHash);
        for (int i = 1; i < steps.Length; i++)
        {
            Assert.Null(steps[i].Request.Snapshot); // not carried again
            Assert.Equal(steps[i - 1].Result!.SnapshotHash, steps[i].Request.SnapshotHash);
            Assert.Equal(steps[i - 1].Result!.StepId, steps[i].Request.SnapshotFrom); // by event: the step whose result recorded it
        }

        Assert.Equal(9, steps.Select(s => s.Result!.StepId).Distinct().Count());
    }

    [Fact]
    public async Task The_session_is_recorded_whole_every_few_steps_and_as_changes_in_between()
    {
        AfHarness h = Support(o => o.FullSnapshotEvery = 3);
        for (int i = 1; i <= 3; i++)
        {
            await h.AskAsync("support", "s", $"question {i}");
        }

        var steps = Steps(await h.HistoryAsync(await h.RunOfAsync("support", "s")));

        string pattern = string.Join("", steps.Select(s => s.Result!.Snapshot is not null ? "W" : "D"));
        Assert.StartsWith("W", pattern, StringComparison.Ordinal);
        Assert.DoesNotContain("DDD", pattern, StringComparison.Ordinal); // never more than FullSnapshotEvery - 1 changes in a row: reading a session applies a few at most
        Assert.True(pattern.Count(c => c == 'D') >= 4, $"most steps should record only a change ({pattern})"); // a session too small for a change to be smaller (the second step) is recorded whole
        for (int i = 1; i < steps.Length; i++)
        {
            if (steps[i].Result!.Delta is not null)
            {
                Assert.Equal(steps[i - 1].Result!.SnapshotHash, steps[i].Result!.BaseHash);
                Assert.True(steps[i].Result!.Delta!.Value.GetRawText().Length < steps[i].Result!.SessionBytes, "a change is recorded only when it is smaller than the session");
                if (i >= 5)
                {
                    Assert.True(steps[i].Result!.Delta!.Value.GetRawText().Length < steps[i].Result!.SessionBytes / 2, "later, a change should be a fraction of the session it changes");
                }
            }
        }
    }

    private static ScriptedChatClient CountingModel(List<int> seen) => new(messages =>
    {
        lock (seen)
        {
            seen.Add(messages.Count);
        }

        return new ChatMessage(ChatRole.Assistant, "ok");
    });

    [Fact]
    public async Task What_the_agent_sees_is_the_whole_conversation_however_the_session_was_recorded()
    {
        var seen = new List<int>();
        var h = new AfHarness(af => af.Configure(o => o.FullSnapshotEvery = 3).AddAgent(AfHarness.AgentWith(CountingModel(seen), "chat")));
        for (int i = 1; i <= 10; i++)
        {
            await h.AskAsync("chat", "s", $"message {i}");
        }

        int first = seen[0];
        Assert.Equal(Enumerable.Range(0, 10).Select(i => first + 2 * i), seen); // each message adds the question and the answer to what the agent is shown
    }

    [Fact]
    public async Task A_worker_that_does_not_hold_the_session_says_so_and_is_sent_it_once()
    {
        var seen = new List<int>();
        var one = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(CountingModel(seen), "chat")));
        await one.AskAsync("chat", "s", "first");
        var elsewhere = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(CountingModel(seen), "chat")), redeployOf: one); // a deploy: a new worker with an empty cache over the same history
        await elsewhere.AskAsync("chat", "s", "second");
        await elsewhere.AskAsync("chat", "s", "third");

        var steps = Steps(await elsewhere.HistoryAsync(await elsewhere.RunOfAsync("chat", "s")));

        Assert.Equal([seen[0], seen[0] + 2, seen[0] + 4], seen); // all three messages saw the conversation so far
        Assert.Single(steps, s => s.Result!.Outcome == AfOutcomes.NeedSnapshot); // asked once; the cache of the new worker held it from then on
        int asked = steps.ToList().FindIndex(s => s.Result!.Outcome == AfOutcomes.NeedSnapshot);
        Assert.Null(steps[asked].Request.Snapshot);
        Assert.NotNull(steps[asked + 1].Request.Snapshot); // the session, sent once
        Assert.Equal(steps[asked].Request.SnapshotHash, steps[asked + 1].Request.SnapshotHash);
        Assert.Null(steps[asked + 2].Request.Snapshot);
    }

    [Fact]
    public async Task A_run_handed_over_to_a_fresh_one_carries_the_session_whole_once_and_goes_on_by_reference()
    {
        AfHarness h = Support(continueAfter: 20);
        for (int i = 1; i <= 6; i++)
        {
            await h.AskAsync("support", "s", $"question {i}");
        }

        var runs = new List<IReadOnlyList<HistoryEvent>>();
        foreach (RunSummary r in await h.Store.ListRunsAsync(AfHarness.Scope, new RunFilter(), 50, default))
        {
            runs.Add(await h.HistoryAsync(r.Run));
        }

        Assert.True(runs.Count >= 2, "the conversation should have been handed over");
        foreach (IReadOnlyList<HistoryEvent> run in runs.Where(x => ((WorkflowStarted)x[0]).PreviousRunId != Guid.Empty))
        {
            AfDriverInput input = AfCodec.Read(((WorkflowStarted)run[0]).Input, AfJson.Default.AfDriverInput);
            Assert.NotNull(input.Snapshot); // whole, once
            var steps = Steps(run);
            Assert.Equal(SnapshotCodec.Hash(SnapshotCodec.Text(SnapshotCodec.Canonical(input.Snapshot!.Value))), steps[0].Request.SnapshotHash);
            Assert.All(steps, s => Assert.Null(s.Request.Snapshot)); // the same worker still holds it: a session is found by what it is, not by the run
            Assert.DoesNotContain(steps, s => s.Result!.Outcome == AfOutcomes.NeedSnapshot);
        }
    }

    // ---- the worker, called directly -------------------------------------------------------------------------------

    private static async Task<AfStepResult> CallAsync(AfHarness h, AfStepRequest request)
    {
        var context = new ActivityContext(new RunKey(AfHarness.Scope.TenantId, AfHarness.Scope.NamespaceId, Guid.NewGuid()), 1, AgentFrameworkNames.Step, 1, "direct-" + Guid.NewGuid().ToString("N"));
        byte[] reply = await h.Runtime.Activities[AgentFrameworkNames.Step](context, AfCodec.Write(request, AfJson.Default.AfStepRequest), CancellationToken.None);
        return AfCodec.Read(reply, AfJson.Default.AfStepResult);
    }

    private static JsonElement Question(string text) => JsonSerializer.SerializeToElement(new List<ChatMessage> { new(ChatRole.User, text) }, AIJsonUtilities.DefaultOptions);

    [Fact]
    public async Task A_step_for_a_session_the_worker_does_not_hold_asks_for_it_and_calls_nothing()
    {
        ScriptedChatClient model = AfHarness.Echo();
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "chat")));

        AfStepResult result = await CallAsync(h, new AfStepRequest { Agent = "chat", TurnId = "t", Protocol = AfProtocol.Referenced, SnapshotHash = new string('a', 64), Messages = Question("hi") });

        Assert.Equal(AfOutcomes.NeedSnapshot, result.Outcome);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task A_session_that_is_not_the_one_its_hash_names_is_refused_and_calls_nothing()
    {
        ScriptedChatClient model = AfHarness.Echo();
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(model, "chat")));
        JsonElement session = JsonDocument.Parse("""{"messages":[]}""").RootElement.Clone();

        await Assert.ThrowsAsync<SnapshotIntegrityException>(async () =>
            await CallAsync(h, new AfStepRequest { Agent = "chat", TurnId = "t", Protocol = AfProtocol.Referenced, Snapshot = session, SnapshotHash = new string('b', 64), Messages = Question("hi") }));
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task A_step_of_the_first_protocol_is_still_served_and_answered_in_the_first_protocol()
    {
        // A run recorded by an earlier release goes on with the old driver while new workers serve its steps (a rolling deploy): the worker must answer in the shape that driver reads.
        var h = new AfHarness(af => af.AddAgent(AfHarness.AgentWith(AfHarness.Echo(), "chat")));

        AfStepResult first = await CallAsync(h, new AfStepRequest { Agent = "chat", TurnId = "t1", Messages = Question("hi") });
        AfStepResult second = await CallAsync(h, new AfStepRequest { Agent = "chat", TurnId = "t2", Snapshot = first.Snapshot, Messages = Question("again") });

        Assert.NotNull(first.Snapshot);
        Assert.Null(first.SnapshotHash);
        Assert.Null(first.Delta);
        Assert.Null(first.StepId);
        Assert.NotNull(second.Snapshot);
        Assert.True(second.Snapshot!.Value.GetRawText().Length > first.Snapshot!.Value.GetRawText().Length);
    }

    [Fact]
    public async Task A_session_too_large_for_the_limit_is_refused_by_name_in_the_new_protocol_too()
    {
        var h = new AfHarness(af => af.Configure(o => o.MaxSnapshotBytes = 200).AddAgent(AfHarness.AgentWith(AfHarness.Echo(new string('x', 600)), "chat")));

        SnapshotTooLargeException ex = await Assert.ThrowsAsync<SnapshotTooLargeException>(async () =>
            await CallAsync(h, new AfStepRequest { Agent = "chat", TurnId = "t", Protocol = AfProtocol.Referenced, Messages = Question("hi") }));

        Assert.Contains("MaxSnapshotBytes", ex.Message, StringComparison.Ordinal);
    }
}
