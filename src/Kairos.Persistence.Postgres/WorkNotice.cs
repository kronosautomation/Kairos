using System.Globalization;
using System.Text.Json.Serialization;

namespace Kairos.Persistence.Postgres;

/// <summary>
/// What the store tells listeners when work appears: which scope, which queue, how many tasks became ready and, for workflow tasks, which build they are pinned to. It is the payload of
/// the <c>kairos_work</c> notification: <c>{tenant}:{namespace}:{queue}:{count}[:{build}]</c> (guids as 32 hex digits, the build last because it may contain colons). A listener wakes at
/// most <see cref="Count"/> waiters of that queue and leaves the rest waiting. An older writer's payload (<c>{tenant}:{namespace}</c>) and a writer that did not count
/// (<see cref="Unknown"/>) mean "something is ready here, ask the store how much".
/// </summary>
/// <param name="TenantId">The tenant.</param>
/// <param name="NamespaceId">The namespace.</param>
/// <param name="Queue"><see cref="Workflow"/>, <see cref="Activity"/> or <see cref="Unknown"/>.</param>
/// <param name="Count">Ready tasks that appeared (0 for <see cref="Unknown"/>).</param>
/// <param name="Build">The build of the workflow tasks, for <see cref="Workflow"/>.</param>
public readonly record struct WorkNotice(Guid TenantId, Guid NamespaceId, char Queue, int Count, string? Build)
{
    /// <summary>Workflow tasks became ready.</summary>
    public const char Workflow = 'w';

    /// <summary>Activity tasks became ready.</summary>
    public const char Activity = 'a';

    /// <summary>Something became ready that the writer did not count.</summary>
    public const char Unknown = '*';

    private const int MaxCount = 1_000_000;
    private const int MaxBuildLength = 128;

    public TenantScope Scope => new(TenantId, NamespaceId);

    /// <summary>A payload that could not be understood at all: the scope is unknown, so a listener should look everywhere it has waiters.</summary>
    public bool IsUnparsed => TenantId == Guid.Empty && NamespaceId == Guid.Empty;

    public static string Format(TenantScope scope, char queue, int count, string? build) =>
        string.Create(CultureInfo.InvariantCulture, $"{scope.TenantId:N}:{scope.NamespaceId:N}:{queue}:{count}{(build is null ? "" : ":" + build)}");

    /// <summary>Reads a payload. False for anything that is not one; a two-part payload from an older writer parses as <see cref="Unknown"/>.</summary>
    public static bool TryParse(string? payload, out WorkNotice notice)
    {
        notice = default;
        if (payload is null)
        {
            return false;
        }

        string[] parts = payload.Split(':', 5);
        if (parts.Length < 2 || !Guid.TryParseExact(parts[0], "N", out Guid tenant) || !Guid.TryParseExact(parts[1], "N", out Guid ns))
        {
            return false;
        }

        if (parts.Length == 2)
        {
            notice = new WorkNotice(tenant, ns, Unknown, 0, null);
            return true;
        }

        if (parts.Length < 4 || parts[2].Length != 1 || parts[2][0] is not (Workflow or Activity or Unknown) ||
            !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int count) || count > MaxCount)
        {
            return false;
        }

        string? build = parts.Length == 5 ? parts[4] : null;
        if (build is { Length: > MaxBuildLength } || (build is not null && build.Any(char.IsControl)))
        {
            return false;
        }

        notice = new WorkNotice(tenant, ns, parts[2][0], count, build);
        return true;
    }
}

[JsonSerializable(typeof(Dictionary<string, int>))]
internal sealed partial class BacklogJson : JsonSerializerContext;
