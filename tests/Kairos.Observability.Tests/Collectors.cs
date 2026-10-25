using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace Kairos.Observability.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryTests
{
    /// <summary>The listeners are process-wide, so these tests run one at a time.</summary>
    public const string Name = "Telemetry (process-wide listeners)";
}

internal sealed record Measured(string Instrument, string Unit, string Description, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>Listens to everything Kairos emits: spans of the <c>Kairos</c> source and measurements of the <c>Kairos</c> meter.</summary>
internal sealed class Collector : IDisposable
{
    private readonly ActivityListener _spans;
    private readonly MeterListener _meters = new();
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ConcurrentQueue<Measured> _points = new();

    public Collector()
    {
        _spans = new ActivityListener
        {
            ShouldListenTo = source => source.Name == KairosTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(_spans);

        _meters.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == KairosMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        _meters.SetMeasurementEventCallback<int>((i, v, tags, _) => Add(i, v, tags));
        _meters.Start();
    }

    public IReadOnlyList<Activity> Spans => [.. _stopped];

    public IReadOnlyList<Measured> Points => [.. _points];

    /// <summary>Asks the observable gauges (queue depth, stuck tasks) for their current values.</summary>
    public void Poll() => _meters.RecordObservableInstruments();

    public IEnumerable<Activity> Named(string name) => Spans.Where(s => s.OperationName == name);

    public double Sum(string instrument, Func<Measured, bool>? where = null) =>
        Points.Where(p => p.Instrument == instrument && (where?.Invoke(p) ?? true)).Sum(p => p.Value);

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, object?>();
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            dict[tag.Key] = tag.Value;
        }

        _points.Enqueue(new Measured(instrument.Name, instrument.Unit ?? "", instrument.Description ?? "", value, dict));
    }

    public void Dispose()
    {
        _spans.Dispose();
        _meters.Dispose();
    }

    /// <summary>
    /// Everything a backend could ever receive from what was collected, as one string: span names, kinds, status descriptions, every tag and tag object, events, links,
    /// baggage, trace state; instrument names, units, descriptions, tags and values.
    /// </summary>
    public string Everything()
    {
        var sb = new StringBuilder();
        foreach (Activity a in Spans)
        {
            sb.Append(a.OperationName).Append('|').Append(a.DisplayName).Append('|').Append(a.Kind).Append('|').Append(a.Status).Append('|').Append(a.StatusDescription).Append('|')
                .Append(a.TraceStateString).Append('|').Append(a.Source.Name).AppendLine();
            foreach (KeyValuePair<string, string?> t in a.Tags)
            {
                sb.Append(t.Key).Append('=').Append(t.Value).AppendLine();
            }

            foreach (KeyValuePair<string, object?> t in a.TagObjects)
            {
                sb.Append(t.Key).Append('=').Append(Render(t.Value)).AppendLine();
            }

            foreach (ActivityEvent e in a.Events)
            {
                sb.Append("event:").Append(e.Name);
                foreach (KeyValuePair<string, object?> t in e.Tags)
                {
                    sb.Append(t.Key).Append('=').Append(Render(t.Value)).Append(';');
                }

                sb.AppendLine();
            }

            foreach (ActivityLink l in a.Links)
            {
                sb.Append("link:").Append(l.Context.TraceId).Append(l.Context.SpanId);
                foreach (KeyValuePair<string, object?> t in l.Tags ?? [])
                {
                    sb.Append(t.Key).Append('=').Append(Render(t.Value)).Append(';');
                }

                sb.AppendLine();
            }

            foreach (KeyValuePair<string, string?> b in a.Baggage)
            {
                sb.Append("baggage:").Append(b.Key).Append('=').Append(b.Value).AppendLine();
            }
        }

        foreach (Measured p in Points)
        {
            sb.Append(p.Instrument).Append('|').Append(p.Unit).Append('|').Append(p.Description).Append('|').Append(p.Value);
            foreach (KeyValuePair<string, object?> t in p.Tags)
            {
                sb.Append('|').Append(t.Key).Append('=').Append(Render(t.Value));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string Render(object? value) =>
        value is System.Collections.IEnumerable list and not string ? string.Join(",", list.Cast<object?>().Select(Render)) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

/// <summary>A distinctive secret and the other ways it could appear in text a backend receives.</summary>
internal static class Canary
{
    public const string Text = "CANARY-7f3a91c2-do-not-leak";

    public static IReadOnlyList<string> Forms { get; } =
    [
        Text,
        Text.ToUpperInvariant(),
        Convert.ToHexString(Encoding.UTF8.GetBytes(Text)),
        Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Text)),
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Text)),
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Text))[..24], // a fragment, in case the text was embedded at a different offset
        Uri.EscapeDataString(Text),
        "7f3a91c2",
    ];

    /// <summary>The forms of the canary found in <paramref name="text"/> (empty when it is clean).</summary>
    public static IReadOnlyList<string> FoundIn(string text) =>
        [.. Forms.Where(f => text.Contains(f, StringComparison.OrdinalIgnoreCase))];
}
