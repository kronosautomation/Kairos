using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework.Tests;

/// <summary>An agent that is not a ChatClientAgent (like an A2A or custom agent): it cannot be run one model call at a time.</summary>
internal sealed class OpaqueAgent(string name, Func<IReadOnlyList<ChatMessage>, int, string> respond) : AIAgent
{
    private int _calls;

    public override string? Name => name;

    public int Calls => Volatile.Read(ref _calls);

    private sealed class OpaqueSession : AgentSession
    {
        public int Turns { get; set; }
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) => new(new OpaqueSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(JsonSerializer.SerializeToElement(new { turns = ((OpaqueSession)session).Turns }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(new OpaqueSession { Turns = serializedState.GetProperty("turns").GetInt32() });

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var s = (OpaqueSession)session!;
        s.Turns++;
        List<ChatMessage> list = [.. messages];
        return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, respond(list, s.Turns))));
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AgentResponse response = await RunCoreAsync(messages, session, options, cancellationToken);
        foreach (AgentResponseUpdate update in response.ToAgentResponseUpdates())
        {
            yield return update;
        }
    }
}
