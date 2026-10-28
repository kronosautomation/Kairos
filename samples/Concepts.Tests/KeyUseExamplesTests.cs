using Kairos.Worker;
using Kairos.Samples.Concepts;
using Microsoft.Extensions.Logging;

namespace Kairos.Samples.Concepts.Tests;

/// <summary>The key-use-log region of the payload-encryption page: a log line per wrap, and none of it holds a key or a payload.</summary>
public sealed class KeyUseExamplesTests
{
    private sealed class Capture : ILogger<LoggingKeyUse>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private sealed class PlainKeys : IKeyProvider
    {
        public ValueTask<WrappedKey> WrapAsync(ReadOnlyMemory<byte> dataKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) => ValueTask.FromResult(new WrappedKey("test-key", dataKey.ToArray()));

        public ValueTask<byte[]> UnwrapAsync(WrappedKey wrapped, ReadOnlyMemory<byte> context, CancellationToken cancellationToken) => ValueTask.FromResult(wrapped.Ciphertext.ToArray());
    }

    [Fact]
    public async Task The_sample_logs_the_wrap_with_the_scope_and_never_the_payload()
    {
        var log = new Capture();
        var scope = new TenantScope(Guid.NewGuid(), Guid.NewGuid());
        using var protector = KeyUseExamples.Protector(new PlainKeys(), log);

        _ = await protector.ProtectAsync(scope, "the secret payload"u8.ToArray(), "ctx"u8.ToArray(), default);

        string line = Assert.Single(log.Lines);
        Assert.Contains(scope.TenantId.ToString(), line, StringComparison.Ordinal);
        Assert.Contains("Wrap", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }
}
