using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kairos;

/// <summary>Renders a <see cref="ReplayCheckReport"/> for people and for pipelines. Both forms contain only what the report contains: verdicts, ids, names, sizes and sequence numbers.</summary>
public static class ReplayCheckReportFormat
{
    /// <summary>A readable summary: the failures with the first diverging command of each, the workflow types and builds not covered, and the coverage table.</summary>
    public static string ToText(this ReplayCheckReport report, bool strictCoverage = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        int notCovered = report.Findings.Count(f => f.Verdict == ReplayVerdict.NotCovered);
        sb.Append(CultureInfo.InvariantCulture, $"Replay check: {report.Checked} replayed, {report.Failures.Count} failed, {notCovered} not covered. ")
          .AppendLine(report.Succeeded(strictCoverage) ? "PASSED" : "FAILED");

        foreach (ReplayFinding f in report.Findings.Where(f => f.IsFailure))
        {
            sb.Append(CultureInfo.InvariantCulture, $"FAIL  {Label(f.Verdict)}  {f.WorkflowType}  build={f.BuildId}  {f.Source}  events={f.Events}").AppendLine();
            if (f.Divergence is { } d)
            {
                sb.Append(CultureInfo.InvariantCulture, $"      first divergence at event {d.Sequence}").AppendLine();
                sb.Append("      recorded: ").AppendLine(d.Recorded);
                sb.Append("      emitted:  ").AppendLine(d.Emitted);
            }
            else
            {
                sb.Append("      ").AppendLine(f.Detail);
            }
        }

        foreach (ReplayFinding f in report.Findings.Where(f => f.Verdict == ReplayVerdict.NotCovered))
        {
            sb.Append(CultureInfo.InvariantCulture, $"NOTE  not covered  {f.WorkflowType}  build={f.BuildId}  {f.Detail}").AppendLine();
        }

        if (report.Checked == 0)
        {
            sb.AppendLine("Nothing was replayed. A check that sees no histories proves nothing: check the scope, the sample and the candidate's workflow registrations.");
        }

        if (report.Coverage.Count > 0)
        {
            sb.AppendLine("Coverage:");
            foreach (ReplayCoverage c in report.Coverage)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {c.WorkflowType}  build={c.BuildId}  open={c.Open} closed={c.Closed} replayed={c.Checked}{(c.InCandidate ? "" : "  (not in the candidate)")}").AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>The same content as JSON, for pipeline steps that parse it.</summary>
    public static string ToJson(this ReplayCheckReport report, bool strictCoverage = false)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteBoolean("succeeded", report.Succeeded(strictCoverage));
            w.WriteNumber("replayed", report.Checked);
            w.WriteNumber("failed", report.Failures.Count);
            w.WriteNumber("notCovered", report.Findings.Count(f => f.Verdict == ReplayVerdict.NotCovered));
            w.WriteStartArray("findings");
            foreach (ReplayFinding f in report.Findings.Where(f => f.Verdict != ReplayVerdict.Passed))
            {
                w.WriteStartObject();
                w.WriteString("verdict", f.Verdict.ToString());
                w.WriteString("source", f.Source);
                w.WriteString("workflowType", f.WorkflowType);
                w.WriteString("buildId", f.BuildId);
                w.WriteNumber("events", f.Events);
                w.WriteString("detail", f.Detail);
                if (f.Divergence is { } d)
                {
                    w.WriteStartObject("firstDivergence");
                    w.WriteNumber("sequence", d.Sequence);
                    w.WriteString("recorded", d.Recorded);
                    w.WriteString("emitted", d.Emitted);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("coverage");
            foreach (ReplayCoverage c in report.Coverage)
            {
                w.WriteStartObject();
                w.WriteString("workflowType", c.WorkflowType);
                w.WriteString("buildId", c.BuildId);
                w.WriteNumber("open", c.Open);
                w.WriteNumber("closed", c.Closed);
                w.WriteNumber("replayed", c.Checked);
                w.WriteBoolean("inCandidate", c.InCandidate);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Label(ReplayVerdict v) => v switch
    {
        ReplayVerdict.Nondeterministic => "nondeterministic",
        ReplayVerdict.IntegrityFailure => "integrity failure",
        ReplayVerdict.Unreadable => "unreadable",
        ReplayVerdict.Error => "error",
        _ => v.ToString().ToLowerInvariant(),
    };
}
