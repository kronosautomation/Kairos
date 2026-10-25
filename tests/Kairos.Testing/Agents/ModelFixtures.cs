using System.Text.Json;
using System.Text.Json.Serialization;
using Kairos.Agents;
using Microsoft.Extensions.AI;

namespace Kairos.Testing.Agents;

/// <summary>How a recorded model call failed. Retries and timeouts in agent workflows are tested against these without a real model.</summary>
public enum FixtureErrorKind
{
    /// <summary>The provider said "slow down" (HTTP 429).</summary>
    RateLimited = 0,

    /// <summary>The provider did not answer in time.</summary>
    Timeout = 1,

    /// <summary>The provider answered with something that is not valid JSON.</summary>
    Malformed = 2,
}

/// <summary>Thrown by the fixture client to play a rate-limit response.</summary>
public sealed class ModelRateLimitedException : Exception
{
    public ModelRateLimitedException()
        : base("The model provider rate-limited the request.")
    {
    }

    public ModelRateLimitedException(string message)
        : base(message)
    {
    }

    public ModelRateLimitedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A conversation the fixture has no recording for. The fixture client never falls back to a real model.</summary>
public sealed class FixtureMissException : Exception
{
    public FixtureMissException()
        : base("No recorded model response matches this conversation.")
    {
    }

    public FixtureMissException(string message)
        : base(message)
    {
    }

    public FixtureMissException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed record FixtureUsage(long? Input, long? Output, long? Total);

internal sealed record FixtureResponse
{
    /// <summary>reply or error.</summary>
    public string Kind { get; init; } = "reply";

    public JsonElement? Messages { get; init; }

    public string? FinishReason { get; init; }

    public FixtureUsage? Usage { get; init; }

    public string? Error { get; init; }

    public string? Message { get; init; }
}

internal sealed record FixtureExchange
{
    public string Fingerprint { get; init; } = "";

    public JsonElement Request { get; init; }

    public FixtureResponse Response { get; init; } = new();
}

internal sealed record FixtureFile
{
    public int Version { get; init; } = 1;

    public List<FixtureExchange> Exchanges { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FixtureFile))]
internal sealed partial class FixtureJson : JsonSerializerContext;

/// <summary>
/// Recorded model exchanges, keyed by the conversation sent. Check the file into the repository; tests then run against exactly what the
/// model said once, with no network and no cost. Several exchanges for one conversation play in order (an error first, then the reply),
/// and the last one repeats for any further call, because a step may legitimately run again after a crash.
/// </summary>
public sealed class ModelFixture
{
    private readonly List<FixtureExchange> _exchanges = [];

    public int Count => _exchanges.Count;

    public static ModelFixture Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FixtureFile file = JsonSerializer.Deserialize(File.ReadAllText(path), FixtureJson.Default.FixtureFile)
            ?? throw new FormatException($"Fixture file {path} is empty.");
        if (file.Version != 1)
        {
            throw new FormatException($"Fixture file {path} has version {file.Version}; this build reads version 1.");
        }

        var fixture = new ModelFixture();
        fixture._exchanges.AddRange(file.Exchanges);
        return fixture;
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, JsonSerializer.Serialize(new FixtureFile { Exchanges = _exchanges }, FixtureJson.Default.FixtureFile));
    }

    /// <summary>Adds an exchange: the model answered <paramref name="reply"/> to <paramref name="request"/>.</summary>
    public ModelFixture AddReply(IReadOnlyList<ChatMessage> request, ChatMessage reply, string finishReason = "stop", long? inputTokens = null, long? outputTokens = null)
    {
        _exchanges.Add(Exchange(request, new FixtureResponse
        {
            Kind = "reply",
            Messages = ToElement(AgentMessageCodec.Serialize([reply])),
            FinishReason = finishReason,
            Usage = inputTokens is null && outputTokens is null ? null : new FixtureUsage(inputTokens, outputTokens, (inputTokens ?? 0) + (outputTokens ?? 0)),
        }));
        return this;
    }

    /// <summary>Adds an exchange where the model asked for a tool.</summary>
    public ModelFixture AddToolCall(IReadOnlyList<ChatMessage> request, string callId, string tool, IDictionary<string, object?>? arguments = null) =>
        AddReply(request, new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, tool, arguments)]), finishReason: "tool_calls");

    /// <summary>Adds an exchange where the provider failed.</summary>
    public ModelFixture AddError(IReadOnlyList<ChatMessage> request, FixtureErrorKind kind, string message = "")
    {
        _exchanges.Add(Exchange(request, new FixtureResponse { Kind = "error", Error = kind.ToString(), Message = message }));
        return this;
    }

    internal IReadOnlyList<FixtureExchange> For(string fingerprint) => [.. _exchanges.Where(e => e.Fingerprint == fingerprint)];

    internal void Record(IReadOnlyList<ChatMessage> request, ChatResponse response)
    {
        var messages = AgentMessageCodec.Serialize(response.Messages);
        _exchanges.Add(Exchange(request, new FixtureResponse
        {
            Kind = "reply",
            Messages = ToElement(messages),
            FinishReason = response.FinishReason?.Value,
            Usage = response.Usage is { } u ? new FixtureUsage(u.InputTokenCount, u.OutputTokenCount, u.TotalTokenCount) : null,
        }));
    }

    private static FixtureExchange Exchange(IReadOnlyList<ChatMessage> request, FixtureResponse response) => new()
    {
        Fingerprint = AgentMessageCodec.Fingerprint(request),
        Request = ToElement(AgentMessageCodec.Serialize(request)),
        Response = response,
    };

    private static JsonElement ToElement(byte[] json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>Plays <see cref="ModelFixture"/> recordings back as an <see cref="IChatClient"/>. Counts calls so tests can prove replay never calls the model.</summary>
public sealed class FixtureChatClient(ModelFixture fixture) : IChatClient
{
    private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);

    /// <summary>How many times the model was called.</summary>
    public int CallCount { get; private set; }

    /// <summary>The options of each call, in order (so tests can check the idempotency key).</summary>
    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        List<ChatMessage> request = [.. messages];
        string fingerprint = AgentMessageCodec.Fingerprint(request);
        IReadOnlyList<FixtureExchange> exchanges = fixture.For(fingerprint);
        if (exchanges.Count == 0)
        {
            throw new FixtureMissException($"No recorded model response matches this conversation (fingerprint {fingerprint[..12]}, {request.Count} messages).");
        }

        int index;
        lock (_calls)
        {
            CallCount++;
            Options.Add(options);
            index = _calls.GetValueOrDefault(fingerprint);
            _calls[fingerprint] = index + 1;
        }

        FixtureResponse response = exchanges[Math.Min(index, exchanges.Count - 1)].Response;
        if (response.Kind == "error")
        {
            throw Enum.Parse<FixtureErrorKind>(response.Error!) switch
            {
                FixtureErrorKind.RateLimited => new ModelRateLimitedException(response.Message ?? "rate limited"),
                FixtureErrorKind.Timeout => new TimeoutException(response.Message ?? "timed out"),
                _ => new JsonException(response.Message ?? "malformed response"),
            };
        }

        IReadOnlyList<ChatMessage> reply = AgentMessageCodec.Deserialize(System.Text.Encoding.UTF8.GetBytes(response.Messages!.Value.GetRawText()));
        var result = new ChatResponse([.. reply])
        {
            FinishReason = response.FinishReason is null ? null : new ChatFinishReason(response.FinishReason),
            Usage = response.Usage is { } u ? new UsageDetails { InputTokenCount = u.Input, OutputTokenCount = u.Output, TotalTokenCount = u.Total } : null,
        };
        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Records what a real model says, so the next run can use <see cref="FixtureChatClient"/>. Run it once, by hand, and check the file in.</summary>
public sealed class RecordingChatClient(IChatClient inner, ModelFixture recording) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        List<ChatMessage> request = [.. messages];
        ChatResponse response = await inner.GetResponseAsync(request, options, cancellationToken);
        recording.Record(request, response);
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose()
    {
    }
}

/// <summary>A model defined by a function of the conversation. Convenient for tests that care about the workflow, not about recorded wording.</summary>
public sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, ChatMessage> respond) : IChatClient
{
    private int _calls;

    public int CallCount => Volatile.Read(ref _calls);

    /// <summary>The idempotency key of each call, in order.</summary>
    public List<string?> Keys { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Interlocked.Increment(ref _calls);
        lock (Keys)
        {
            Keys.Add(options?.AdditionalProperties?.TryGetValue(AgentsRuntime.IdempotencyKeyProperty, out object? k) == true ? k as string : null);
        }

        ChatMessage reply = respond([.. messages]);
        return Task.FromResult(new ChatResponse(reply)
        {
            FinishReason = reply.Contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop,
            Usage = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5, TotalTokenCount = 15 },
        });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
