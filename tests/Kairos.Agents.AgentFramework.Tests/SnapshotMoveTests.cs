using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>
/// A message handed from one executor's queue to the next is the same value under another name. A change of a workflow's checkpoint says so (a reference to where the value was) instead of writing it out again; a session's changes never do.
/// </summary>
public sealed class SnapshotMoveTests
{
    private static JsonNode Parse(string json) => SnapshotCodec.Canonical(JsonDocument.Parse(json).RootElement);

    private static string Blob(int seed, int bytes = 600) => string.Concat(Enumerable.Range(0, bytes / 12 + 1).Select(i => $"w{seed}-{i:D4}-xyz ")).Substring(0, bytes);

    private static JsonNode Chat(int messages, int seed = 1) => new JsonArray([.. Enumerable.Range(0, messages).Select(i => (JsonNode?)new JsonObject { ["role"] = "assistant", ["text"] = Blob(seed * 100 + i) })]);

    private static JsonNode Queue(string executor, JsonNode chat) =>
        new JsonObject { ["runnerData"] = new JsonObject { ["queuedMessages"] = new JsonObject { [executor] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["typeId"] = "list", ["value"] = chat }, ["source"] = new JsonObject { ["id"] = "x" } }) } }, ["stepNumber"] = 1 };

    private static void Roundtrip(JsonNode before, JsonNode after, bool allowMoves, out JsonNode change)
    {
        JsonNode canonicalBefore = SnapshotCodec.Canonical(SnapshotCodec.Element(before.ToJsonString()));
        JsonNode canonicalAfter = SnapshotCodec.Canonical(SnapshotCodec.Element(after.ToJsonString()));
        change = SnapshotCodec.Diff(canonicalBefore, canonicalAfter, allowMoves) ?? new JsonObject { ["e"] = 1 };
        JsonNode? applied = SnapshotCodec.Apply(canonicalBefore, change);
        Assert.Equal(SnapshotCodec.Text(canonicalAfter), SnapshotCodec.Text(applied!));
    }

    [Fact]
    public void A_value_that_moved_to_another_name_is_a_reference_to_where_it_was()
    {
        JsonNode before = Queue("g1", Chat(6));
        JsonNode after = Queue("g2", Chat(6));

        Roundtrip(before, after, allowMoves: true, out JsonNode change);

        string text = change.ToJsonString();
        Assert.Contains("\"m\"", text, StringComparison.Ordinal);
        Assert.True(text.Length < SnapshotCodec.Text(after).Length / 4, $"the change is {text.Length} bytes for a value of {SnapshotCodec.Text(after).Length}");
    }

    [Fact]
    public void A_value_that_moved_and_grew_is_a_reference_and_the_new_messages()
    {
        JsonNode before = Queue("g1", Chat(6));
        JsonArray grown = (JsonArray)Chat(6);
        grown.Add(new JsonObject { ["role"] = "assistant", ["text"] = Blob(999) });
        JsonNode after = Queue("g2", grown);

        Roundtrip(before, after, allowMoves: true, out JsonNode change);

        string text = change.ToJsonString();
        Assert.Contains(Blob(999), text, StringComparison.Ordinal); // the new message is written out ...
        Assert.DoesNotContain(Blob(100), text, StringComparison.Ordinal); // ... the old ones are not
        Assert.True(text.Length < 2 * 700, $"the change is {text.Length} bytes");
    }

    [Fact]
    public void Without_the_option_a_change_is_what_it_always_was_and_never_has_a_reference()
    {
        JsonNode before = Queue("g1", Chat(6));
        JsonNode after = Queue("g2", Chat(7));

        Roundtrip(before, after, allowMoves: false, out JsonNode change);

        string text = change.ToJsonString();
        Assert.DoesNotContain("\"m\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"w\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"a\"", text, StringComparison.Ordinal);
        Assert.Equal(SnapshotCodec.Diff(Parse(before.ToJsonString()), Parse(after.ToJsonString()))!.ToJsonString(), SnapshotCodec.Diff(Parse(before.ToJsonString()), Parse(after.ToJsonString()), allowMoves: false)!.ToJsonString());
    }

    [Fact]
    public void A_small_value_is_not_worth_a_reference()
    {
        JsonNode before = Parse("""{"a":{"x":[1,2,3]}}""");
        JsonNode after = Parse("""{"b":{"x":[1,2,3]}}""");

        Roundtrip(before, after, allowMoves: true, out JsonNode change);

        Assert.DoesNotContain("\"m\"", change.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_value_in_two_places_is_found_in_either()
    {
        JsonNode chat = Chat(4);
        JsonNode before = new JsonObject { ["one"] = chat.DeepClone(), ["two"] = chat.DeepClone() };
        JsonNode after = new JsonObject { ["one"] = chat.DeepClone(), ["two"] = chat.DeepClone(), ["three"] = chat.DeepClone() };

        Roundtrip(before, after, allowMoves: true, out JsonNode change);

        Assert.True(change.ToJsonString().Length < 100, change.ToJsonString());
    }

    [Fact]
    public void A_reference_that_points_at_nothing_is_refused()
    {
        JsonNode before = Parse("""{"a":[1]}""");

        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(before, JsonNode.Parse("""{"o":{"b":{"m":["zz",0]}}}""")!));
        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(before, JsonNode.Parse("""{"o":{"b":{"m":["a",5]}}}""")!));
        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(before, JsonNode.Parse("""{"o":{"b":{"m":"a"}}}""")!));
        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(before, JsonNode.Parse("""{"o":{"b":{"w":5}}}""")!));
        Assert.Throws<SnapshotIntegrityException>(() => SnapshotCodec.Apply(before, JsonNode.Parse("""{"o":{"b":{"a":{}}}}""")!));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void A_change_applied_to_the_snapshot_before_gives_the_snapshot_after_when_values_move_grow_and_are_copied(int seed)
    {
        var random = new Random(seed);
        string[] names = ["a", "b", "c", "d", "e", "f"];
        for (int round = 0; round < 300; round++)
        {
            JsonNode Make() => new JsonObject(Enumerable.Range(0, random.Next(1, 5)).Select(_ => names[random.Next(names.Length)]).Distinct().Select(n =>
                new KeyValuePair<string, JsonNode?>(n, random.Next(3) switch
                {
                    0 => Chat(random.Next(1, 5), random.Next(3)),
                    1 => new JsonObject { ["inner"] = Chat(random.Next(1, 4), random.Next(3)), ["n"] = random.Next(9) },
                    _ => random.Next(100),
                })));

            JsonNode before = Make();
            JsonNode after = Make();
            if (random.Next(2) == 0)
            {
                // some of what was there is still there, under other names, and grown
                var shaken = new JsonObject();
                foreach (KeyValuePair<string, JsonNode?> member in before.AsObject())
                {
                    shaken[names[random.Next(names.Length)]] = member.Value?.DeepClone();
                }

                foreach (KeyValuePair<string, JsonNode?> member in after.AsObject())
                {
                    shaken[member.Key] = member.Value?.DeepClone();
                }

                after = shaken;
            }

            Roundtrip(before, after, allowMoves: true, out _);
        }
    }
}
