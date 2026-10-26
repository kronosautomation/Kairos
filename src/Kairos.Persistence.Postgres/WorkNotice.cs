using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// What the store tells listeners when work appears. <b>It tells nothing a bystander could use.</b> <c>LISTEN</c> is not covered by row level security: any connection to the database can
/// listen on the channel and read every payload. So the payload is <c>{tag}:{count}</c> and nothing else, where the tag is the first 16 bytes (hex) of HMAC-SHA256, under a key only the
/// application processes of a deployment hold, over the tenant, the namespace and the queue. A listener computes the tags of the scopes it serves and recognises them; everyone else sees
/// opaque, stable hex. Which build a task is pinned to is never sent: a listener that needs the breakdown asks the store, through the ordinary row-level-security-scoped backlog query.
/// A notice is only a hint (a lost one delays, never loses, work), so a process with a different key simply does not recognise it and falls back to its other wake-ups.
/// </summary>
/// <param name="Tag">The opaque scope-and-queue tag, 32 hex digits; empty for a payload that could not be understood.</param>
/// <param name="Count">Ready tasks that appeared (0 when the writer did not count).</param>
public readonly record struct WorkNotice(string Tag, int Count)
{
    /// <summary>Workflow tasks became ready.</summary>
    public const char Workflow = 'w';

    /// <summary>Activity tasks became ready.</summary>
    public const char Activity = 'a';

    /// <summary>Something became ready that the writer did not count (a fired timer, a reaped lease, a resumed task): ask the store how much.</summary>
    public const char Unknown = '*';

    /// <summary>Shortest key accepted.</summary>
    public const int MinimumKeyBytes = 32;

    private const int TagBytes = 16;
    private const int MaxCount = 1_000_000;

    /// <summary>A payload that could not be understood at all: a listener should look everywhere it has waiters.</summary>
    public bool IsUnparsed => Tag.Length == 0;

    /// <summary>The tag of a scope and queue under the deployment notification key.</summary>
    public static string TagOf(ReadOnlySpan<byte> key, TenantScope scope, char queue)
    {
        if (key.Length < MinimumKeyBytes)
        {
            throw new ArgumentException($"The notification key must be at least {MinimumKeyBytes} bytes.", nameof(key));
        }

        Span<byte> message = stackalloc byte[32 + 1 + 32 + 1 + 1];
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{scope.TenantId:N}:{scope.NamespaceId:N}:{queue}"), message);
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(key, message, mac);
        return Convert.ToHexStringLower(mac[..TagBytes]);
    }

    public static string Format(ReadOnlySpan<byte> key, TenantScope scope, char queue, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"{TagOf(key, scope, queue)}:{count}");

    /// <summary>Reads a payload. False for anything that is not exactly 32 lowercase hex digits, a colon and a count.</summary>
    public static bool TryParse(string? payload, out WorkNotice notice)
    {
        notice = default;
        if (payload is null || payload.Length < 34 || payload.Length > 42 || payload[32] != ':')
        {
            return false;
        }

        foreach (char c in payload.AsSpan(0, 32))
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        if (!int.TryParse(payload.AsSpan(33), NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count > MaxCount)
        {
            return false;
        }

        notice = new WorkNotice(payload[..32], count);
        return true;
    }
}

[JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class BacklogJson : JsonSerializerContext;
