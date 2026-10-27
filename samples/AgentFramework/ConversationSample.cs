using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace Kairos.Samples.AgentFramework;

/// <summary>An agent for long conversations: the history its session keeps is compacted as messages are added, so a conversation of a hundred messages is recorded no larger than one of twenty.</summary>
public static class ConversationSample
{
    #region compaction
    /// <summary>
    /// The history provider for a conversation that goes on: once old tool output adds up to more than 8,000 tokens it is collapsed to a line per call, and once there are more than 20 turns the oldest drop out of the
    /// session, keeping at least the last 10. The reduction runs when a message is added, so what the session keeps shrinks, and with it what every step records.
    /// </summary>
    public static InMemoryChatHistoryProvider LongConversationHistory() => new(new InMemoryChatHistoryProviderOptions
    {
        ChatReducer = new PipelineCompactionStrategy(
            new ToolResultCompactionStrategy(CompactionTriggers.TokensExceed(8_000), minimumPreservedGroups: 4),
            new SlidingWindowCompactionStrategy(CompactionTriggers.TurnsExceed(20), minimumPreservedTurns: 10)).AsChatReducer(),
        ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
    });
    #endregion

    #region compacted-agent
    /// <summary>The usual agent, with the compacting history. Everything else about it, and about making it durable, is as before.</summary>
    public static ChatClientAgent Create(IChatClient model, params AITool[] tools) =>
        new(model, new ChatClientAgentOptions
        {
            Name = "support",
            Description = "Answers questions about orders for as long as the customer wants to talk.",
            ChatOptions = new ChatOptions { Instructions = "You help customers with their orders.", Tools = [.. tools] },
            ChatHistoryProvider = LongConversationHistory(),
        });
    #endregion
}
