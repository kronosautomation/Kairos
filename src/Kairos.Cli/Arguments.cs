using System.Globalization;

namespace Kairos.Cli;

/// <summary>The options of <c>kairos replay-check</c>.</summary>
internal sealed record ReplayCheckArguments
{
    /// <summary>Path of the assembly of the candidate build.</summary>
    public string Build { get; init; } = "";

    /// <summary>The <see cref="IReplayCheckSetup"/> to use when the assembly names none (or several).</summary>
    public string? Setup { get; init; }

    /// <summary>Name of the environment variable holding the connection string. The default keeps it out of command lines and logs.</summary>
    public string ConnectionVariable { get; init; } = "KAIROS_CONNECTION";

    public string? Connection { get; init; }

    public TenantScope Scope { get; init; } = KairosOptions.DefaultScope;

    public int Sample { get; init; } = 25;

    public DateTimeOffset? Since { get; init; }

    public string? Corpus { get; init; }

    public bool StrictCoverage { get; init; }

    public bool Json { get; init; }

    /// <summary>Parses the arguments after <c>replay-check</c>. Throws <see cref="ArgumentException"/> with a message for the user.</summary>
    public static ReplayCheckArguments Parse(IReadOnlyList<string> args, DateTimeOffset now)
    {
        var result = new ReplayCheckArguments();
        Guid? tenant = null, ns = null;
        for (int i = 0; i < args.Count; i++)
        {
            string name = args[i];
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{name} needs a value.");
            switch (name)
            {
                case "--build":
                    result = result with { Build = Value() };
                    break;
                case "--setup":
                    result = result with { Setup = Value() };
                    break;
                case "--connection":
                    result = result with { Connection = Value() };
                    break;
                case "--connection-env":
                    result = result with { ConnectionVariable = Value() };
                    break;
                case "--tenant":
                    tenant = ParseGuid(name, Value());
                    break;
                case "--namespace":
                    ns = ParseGuid(name, Value());
                    break;
                case "--sample":
                    result = result with { Sample = ParseSample(Value()) };
                    break;
                case "--since":
                    result = result with { Since = ParseSince(Value(), now) };
                    break;
                case "--corpus":
                    result = result with { Corpus = Value() };
                    break;
                case "--strict-coverage":
                    result = result with { StrictCoverage = true };
                    break;
                case "--format":
                    string format = Value();
                    result = result with
                    {
                        Json = format switch
                        {
                            "json" => true,
                            "text" => false,
                            _ => throw new ArgumentException("--format is text or json."),
                        },
                    };
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'.");
            }
        }

        if (tenant is null != ns is null)
        {
            throw new ArgumentException("--tenant and --namespace go together.");
        }

        if (tenant is { } t && ns is { } n)
        {
            result = result with { Scope = new TenantScope(t, n) };
        }

        return string.IsNullOrEmpty(result.Build) ? throw new ArgumentException("--build <path to the assembly of the candidate build> is required.") : result;
    }

    private static Guid ParseGuid(string option, string value) =>
        Guid.TryParse(value, out Guid g) ? g : throw new ArgumentException($"{option} must be a GUID.");

    private static int ParseSample(string value)
    {
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : throw new ArgumentException("--sample is a positive number or 'all'.");
    }

    /// <summary>An ISO date or time, or a span back from now: <c>30m</c>, <c>12h</c>, <c>7d</c>.</summary>
    internal static DateTimeOffset ParseSince(string value, DateTimeOffset now)
    {
        if (value.Length >= 2 && int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out int amount))
        {
            TimeSpan? span = value[^1] switch
            {
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => null,
            };
            if (span is { } s)
            {
                return now - s;
            }
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at)
            ? at
            : throw new ArgumentException("--since is a date or time, or a span back from now such as 30m, 12h or 7d.");
    }
}
