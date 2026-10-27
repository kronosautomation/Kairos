using Microsoft.Extensions.AI;

namespace Kairos.Samples.AgentFramework;

/// <summary>
/// Stands in for a model so the sample runs without an account: it looks the order up, then asks for the refund, then answers. In your application the agent is given your provider's
/// <see cref="IChatClient"/>, and nothing else here changes.
/// </summary>
public sealed class DemoModel : IChatClient
{
    private static readonly UsageDetails Usage = new() { InputTokenCount = 120, OutputTokenCount = 30, TotalTokenCount = 150 };

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        int results = messages.Sum(m => m.Contents.OfType<FunctionResultContent>().Count());
        ChatMessage reply = results switch
        {
            0 => Call("call-1", "lookup_order"),
            1 => Call("call-2", "issue_refund"),
            _ => new ChatMessage(ChatRole.Assistant, "Your order A-1042 has been refunded."),
        };
        return Task.FromResult(new ChatResponse(reply) { Usage = Usage });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static ChatMessage Call(string id, string tool) =>
        new(ChatRole.Assistant, [new FunctionCallContent(id, tool, new Dictionary<string, object?> { ["orderId"] = "A-1042" })]);
}
