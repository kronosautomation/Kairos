using System.Net;
using System.Security.Cryptography;
using Kairos.Cloud.SelfHosted;
using Kairos.Conformance;
using Kairos.Testing;

namespace Kairos.SelfHosted.Tests;

// Each adapter runs the shared conformance suite for its interface; adapter-specific behaviour is tested separately below.

public sealed class LocalKeyProviderConformanceTests : KeyProviderConformance
{
    private readonly Dictionary<string, byte[]> _keys = new() { ["k1"] = RandomNumberGenerator.GetBytes(32) };

    protected override IKeyProvider CreateProvider() => new LocalKeyProvider(_keys, "k1");

    protected override IKeyProvider? CreateRotated(IKeyProvider original) =>
        new LocalKeyProvider(new Dictionary<string, byte[]>(_keys) { ["k2"] = RandomNumberGenerator.GetBytes(32) }, "k2");
}

public sealed class FileBlobOffloadConformanceTests : BlobOffloadConformance, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kairos-blob-" + Guid.NewGuid().ToString("N"));

    protected override IBlobOffload CreateOffload() => new FileBlobOffload(_root);

    protected override Task<bool> CorruptAsync(TenantScope scope, BlobRef reference)
    {
        string[] files = Directory.GetFiles(_root, reference.Sha256Hex, SearchOption.AllDirectories);
        if (files.Length != 1)
        {
            return Task.FromResult(false);
        }

        File.AppendAllText(files[0], "!");
        return Task.FromResult(true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

public sealed class ApiKeyAuthenticatorConformanceTests : AuthenticatorConformance
{
    private readonly byte[] _pepper = RandomNumberGenerator.GetBytes(32);

    protected override IWorkerAuthenticator CreateAuthenticator() => new ApiKeyAuthenticator(_pepper);

    protected override Task<Credential> IssueAsync(IWorkerAuthenticator authenticator, TenantScope scope, Role role, string subject, TimeSpan lifetime) =>
        Task.FromResult<Credential>(new BearerCredential(((ApiKeyAuthenticator)authenticator).Issue(scope, role, subject, Now, lifetime)));

    protected override Task<bool> RevokeAsync(IWorkerAuthenticator authenticator, Credential credential) =>
        Task.FromResult(((ApiKeyAuthenticator)authenticator).Revoke(((BearerCredential)credential).Secret));
}

public sealed class SignedTokenAuthenticatorConformanceTests : AuthenticatorConformance
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    protected override IWorkerAuthenticator CreateAuthenticator() => new SignedTokenAuthenticator(_key);

    protected override Task<Credential> IssueAsync(IWorkerAuthenticator authenticator, TenantScope scope, Role role, string subject, TimeSpan lifetime) =>
        Task.FromResult<Credential>(new BearerCredential(((SignedTokenAuthenticator)authenticator).Issue(new Principal(scope, role, subject, null), Now, lifetime)));
}

public sealed class InMemoryEventSinkConformanceTests : EventSinkConformance
{
    private readonly InMemoryEventSink _sink = new();

    protected override IEventSink CreateSink() => _sink;

    protected override Task<IReadOnlyList<RunEventNotice>> ReceivedAsync() => Task.FromResult(_sink.Published);
}

public sealed class WebhookEventSinkConformanceTests : EventSinkConformance
{
    private static readonly byte[] Secret = RandomNumberGenerator.GetBytes(32);
    private readonly List<RunEventNotice> _received = [];
    private int _failuresLeft;

    private sealed class Receiver(WebhookEventSinkConformanceTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (owner._failuresLeft > 0)
            {
                owner._failuresLeft--;
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            byte[] body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            string timestamp = request.Headers.GetValues(WebhookEventSink.TimestampHeader).Single();
            string signature = request.Headers.GetValues(WebhookEventSink.SignatureHeader).Single();
            if (!WebhookEventSink.Verify(Secret, timestamp, signature, body, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5)))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            owner._received.AddRange(WebhookEventSink.Parse(body));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    protected override IEventSink CreateSink() =>
        new WebhookEventSink(new HttpClient(new Receiver(this)), new Uri("https://hooks.example.test/kairos"), Secret);

    protected override Task<IReadOnlyList<RunEventNotice>> ReceivedAsync() => Task.FromResult<IReadOnlyList<RunEventNotice>>([.. _received]);

    protected override Task<bool> FailNextDeliveriesAsync(int count)
    {
        _failuresLeft = count;
        return Task.FromResult(true);
    }
}

public sealed class InMemoryInboxConformanceTests : EventSourceConformance
{
    private static readonly TimeSpan Visibility = TimeSpan.FromSeconds(30);
    private readonly VirtualTimeProvider _time = new();
    private InMemoryInbox? _inbox;

    protected override IEventSource CreateSource() => _inbox = new InMemoryInbox(_time, Visibility);

    protected override Task EnqueueAsync(IEventSource source, InboundEvent inbound)
    {
        ((InMemoryInbox)source).Enqueue(inbound);
        return Task.CompletedTask;
    }

    protected override Task PassRedeliveryTimeoutAsync(IEventSource source)
    {
        _time.Advance(Visibility + TimeSpan.FromSeconds(1));
        return Task.CompletedTask;
    }
}
