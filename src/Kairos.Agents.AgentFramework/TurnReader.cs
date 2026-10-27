using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kairos.Agents.AgentFramework;

/// <summary>
/// What is known of the answer to one message from the events of its run seen so far. Events are added as they are read (the ones after the last look), so a look costs what is new: the steps of this message are
/// recognised when they are scheduled, and their results counted when they complete, possibly in a later look.
/// </summary>
internal sealed class TurnReader(string turnId)
{
    private readonly HashSet<int> _mine = [];
    private long _input;
    private long _output;
    private long _total;
    private AfStepResult? _last;

    /// <summary>The last event added; of a closed run, the one that closed it.</summary>
    public HistoryEvent? Last { get; private set; }

    public void Add(IReadOnlyList<HistoryEvent> events)
    {
        foreach (HistoryEvent e in events)
        {
            Last = e;
            if (e is ActivityScheduled s && (s.ActivityType == AgentFrameworkNames.Step || s.ActivityType == AgentFrameworkNames.Turn))
            {
                if (AfCodec.Read(s.Input, AfJson.Default.AfStepRequest).TurnId == turnId)
                {
                    _mine.Add(s.ActivityId);
                }
            }
            else if (e is ActivityCompleted c && _mine.Contains(c.ActivityId))
            {
                AfStepResult result = AfCodec.Read(c.Result, AfJson.Default.AfStepResult);
                if (result.Outcome != AfOutcomes.Done)
                {
                    continue;
                }

                _input += result.InputTokens ?? 0;
                _output += result.OutputTokens ?? 0;
                _total += result.TotalTokens ?? 0;
                if (result.Final)
                {
                    _last = result;
                }
            }
        }
    }

    /// <summary>The answer, or null while the message has no final step.</summary>
    public AgentResponse? Reply(AgentDefinition agent)
    {
        if (_last?.Messages is not { } messages)
        {
            return null;
        }

        List<ChatMessage> produced = JsonSerializer.Deserialize<List<ChatMessage>>(messages, AIJsonUtilities.DefaultOptions) ?? [];
        return new AgentResponse(produced)
        {
            AgentId = agent.Name,
            ResponseId = turnId,
            Usage = new UsageDetails { InputTokenCount = _input, OutputTokenCount = _output, TotalTokenCount = _total },
            FinishReason = _last.FinishReason is { } f ? new ChatFinishReason(f) : null,
        };
    }
}
