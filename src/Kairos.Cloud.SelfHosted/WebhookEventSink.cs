using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Kairos.Cloud.SelfHosted;

/// <summary>
/// Publishes run events to an HTTP endpoint. Each request is signed (<c>X-Kairos-Signature: sha256=HMAC(secret, timestamp.body)</c>,
/// <c>X-Kairos-Timestamp</c>) so receivers can authenticate it and reject replays. Delivery is at-least-once: transient failures are
/// retried with backoff, so receivers de-duplicate on the stable event id. Bodies carry metadata only, never payloads.
/// </summary>
public sealed class WebhookEventSink : IEventSink
{
    public const string SignatureHeader = "X-Kairos-Signature";
    public const string TimestampHeader = "X-Kairos-Timestamp";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly byte[] _secret;
    private readonly TimeProvider _time;
    private readonly int _maxAttempts;

    public WebhookEventSink(HttpClient http, Uri endpoint, byte[] secret, TimeProvider? time = null, int maxAttempts = 4)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentOutOfRangeException.ThrowIfLessThan(secret.Length, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _http = http;
        _endpoint = endpoint;
        _secret = [.. secret];
        _time = time ?? TimeProvider.System;
        _maxAttempts = maxAttempts;
    }

    public async ValueTask PublishAsync(IReadOnlyList<RunEventNotice> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return;
        }

        byte[] body = Serialize(events);
        for (int attempt = 1; ; attempt++)
        {
            string timestamp = _time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
            request.Headers.TryAddWithoutValidation(SignatureHeader, Sign(_secret, timestamp, body));

            HttpStatusCode? status = null;
            try
            {
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                status = response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // network failure: retried below
            }

            bool permanent = status is { } s && (int)s is >= 400 and < 500 && s is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
            if (permanent || attempt >= _maxAttempts)
            {
                throw new HttpRequestException($"Event delivery to the webhook failed after {attempt} attempt(s) (status {(status is null ? "none" : (int)status)}).");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string Sign(byte[] secret, string timestamp, ReadOnlySpan<byte> body)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        byte[] message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, message));
    }

    /// <summary>Receiver side: true when the signature matches and the timestamp is within <paramref name="tolerance"/> of <paramref name="now"/>.</summary>
    public static bool Verify(byte[] secret, string? timestamp, string? signature, ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (timestamp is null || signature is null || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
        {
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > tolerance)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, timestamp, body)), Encoding.ASCII.GetBytes(signature));
    }

    public static byte[] Serialize(IReadOnlyList<RunEventNotice> events)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteStartArray("events");
            foreach (RunEventNotice e in events)
            {
                w.WriteStartObject();
                w.WriteString("id", e.EventId);
                w.WriteString("tenantId", e.Run.TenantId);
                w.WriteString("namespaceId", e.Run.NamespaceId);
                w.WriteString("runId", e.Run.RunId);
                w.WriteString("type", e.Type.ToString());
                w.WriteNumber("sequence", e.Sequence);
                w.WriteString("at", e.At);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static IReadOnlyList<RunEventNotice> Parse(ReadOnlySpan<byte> body)
    {
        var events = new List<RunEventNotice>();
        using JsonDocument doc = JsonDocument.Parse(body.ToArray());
        foreach (JsonElement e in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            var run = new RunKey(e.GetProperty("tenantId").GetGuid(), e.GetProperty("namespaceId").GetGuid(), e.GetProperty("runId").GetGuid());
            events.Add(new RunEventNotice(
                e.GetProperty("id").GetString()!, run, Enum.Parse<HistoryEventType>(e.GetProperty("type").GetString()!),
                e.GetProperty("sequence").GetInt64(), e.GetProperty("at").GetDateTimeOffset()));
        }

        return events;
    }
}
