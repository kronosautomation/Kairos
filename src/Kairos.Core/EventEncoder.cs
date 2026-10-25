using System.Buffers.Binary;
using System.Text;

namespace Kairos;

/// <summary>
/// Canonical, versioned binary encoding of a history event used as hash input. Writes straight into a caller-supplied
/// span (no intermediate allocations). Fields are length-prefixed so no two distinct events share an encoding.
/// </summary>
internal static class EventEncoder
{
    internal const byte EncodingVersion = 1;
    private const int HeaderSize = 1 + 1 + (3 * 16) + 8 + 8;

    /// <summary>Upper bound of the encoded size of <paramref name="e"/>.</summary>
    public static int MaxSize(HistoryEvent e) => HeaderSize + e switch
    {
        WorkflowStarted x => StringMax(x.WorkflowType) + StringMax(x.BuildId) + BytesMax(x.Input) + 16 + TraceMax(x.TraceContext),
        WorkflowContinued x => 16 + BytesMax(x.Input),
        PatchMarker x => StringMax(x.PatchId),
        ActivityScheduled x => 4 + StringMax(x.ActivityType) + BytesMax(x.Input) + PolicyMax(x.RetryPolicy) + TraceMax(x.TraceContext),
        ActivityCompleted x => 4 + BytesMax(x.Result),
        ActivityFailed x => 4 + 4 + StringMax(x.ErrorType) + StringMax(x.Message) + 1,
        TimerStarted => 4 + 8,
        TimerFired => 4,
        WorkflowTaskCompleted => 0,
        RunTerminated x => StringMax(x.Reason),
        SignalReceived x => StringMax(x.SignalName) + BytesMax(x.Payload) + (x.SignalId.Length == 0 && x.TraceContext.Length == 0 ? 0 : StringMax(x.SignalId)) + TraceMax(x.TraceContext),
        WorkflowCompleted x => BytesMax(x.Result),
        WorkflowFailed x => StringMax(x.ErrorType) + StringMax(x.Message),
        _ => throw new ArgumentException($"Unknown history event type {e.GetType().Name}.", nameof(e)),
    };

    /// <summary>Encodes <paramref name="e"/> into <paramref name="destination"/> and returns the bytes written.</summary>
    public static int Write(HistoryEvent e, Span<byte> destination)
    {
        var w = new SpanWriter(destination);
        w.WriteByte(EncodingVersion);
        w.WriteByte((byte)e.Type);
        w.WriteGuid(e.TenantId);
        w.WriteGuid(e.NamespaceId);
        w.WriteGuid(e.RunId);
        w.WriteInt64(e.Sequence);
        w.WriteInt64(e.Timestamp.UtcTicks);

        switch (e)
        {
            case WorkflowStarted x:
                w.WriteString(x.WorkflowType);
                w.WriteString(x.BuildId);
                w.WriteBytes(x.Input);
                w.WriteGuid(x.PreviousRunId);
                WriteTrace(ref w, x.TraceContext);
                break;
            case WorkflowContinued x:
                w.WriteGuid(x.NewRunId);
                w.WriteBytes(x.Input);
                break;
            case PatchMarker x:
                w.WriteString(x.PatchId);
                break;
            case ActivityScheduled x:
                w.WriteInt32(x.ActivityId);
                w.WriteString(x.ActivityType);
                w.WriteBytes(x.Input);
                w.WriteInt32(x.RetryPolicy.MaximumAttempts);
                w.WriteInt64(x.RetryPolicy.InitialInterval.Ticks);
                w.WriteInt64(BitConverter.DoubleToInt64Bits(x.RetryPolicy.BackoffCoefficient));
                w.WriteInt64(x.RetryPolicy.MaximumInterval.Ticks);
                w.WriteInt32(x.RetryPolicy.NonRetryableErrorTypes.Count);
                for (int i = 0; i < x.RetryPolicy.NonRetryableErrorTypes.Count; i++)
                {
                    w.WriteString(x.RetryPolicy.NonRetryableErrorTypes[i]);
                }

                WriteTrace(ref w, x.TraceContext);
                break;
            case ActivityCompleted x:
                w.WriteInt32(x.ActivityId);
                w.WriteBytes(x.Result);
                break;
            case ActivityFailed x:
                w.WriteInt32(x.ActivityId);
                w.WriteInt32(x.Attempt);
                w.WriteString(x.ErrorType);
                w.WriteString(x.Message);
                w.WriteByte(x.WillRetry ? (byte)1 : (byte)0);
                break;
            case TimerStarted x:
                w.WriteInt32(x.TimerId);
                w.WriteInt64(x.Duration.Ticks);
                break;
            case TimerFired x:
                w.WriteInt32(x.TimerId);
                break;
            case WorkflowTaskCompleted:
                break;
            case RunTerminated x:
                w.WriteString(x.Reason);
                break;
            case SignalReceived x:
                w.WriteString(x.SignalName);
                w.WriteBytes(x.Payload);
                if (x.SignalId.Length > 0 || x.TraceContext.Length > 0)
                {
                    // Trailing and optional: histories written before signal ids (or tracing) keep their exact encoding and hashes. A signal that carries a
                    // trace context always writes its id first (possibly empty), so the two optional fields can never be mistaken for one another.
                    w.WriteString(x.SignalId);
                    WriteTrace(ref w, x.TraceContext);
                }

                break;
            case WorkflowCompleted x:
                w.WriteBytes(x.Result);
                break;
            case WorkflowFailed x:
                w.WriteString(x.ErrorType);
                w.WriteString(x.Message);
                break;
            default:
                throw new ArgumentException($"Unknown history event type {e.GetType().Name}.", nameof(e));
        }

        return w.Position;
    }

    private static int TraceMax(string trace) => trace.Length == 0 ? 0 : StringMax(trace);

    /// <summary>The trace context is trailing and optional, so events written before tracing keep their exact encoding and hashes.</summary>
    private static void WriteTrace(ref SpanWriter w, string trace)
    {
        if (trace.Length > 0)
        {
            w.WriteString(trace);
        }
    }

    private static int StringMax(string s) => 4 + Encoding.UTF8.GetMaxByteCount(s.Length);

    private static int BytesMax(byte[] b) => 4 + b.Length;

    private static int PolicyMax(RetryPolicy p)
    {
        int size = 4 + 8 + 8 + 8 + 4;
        for (int i = 0; i < p.NonRetryableErrorTypes.Count; i++)
        {
            size += StringMax(p.NonRetryableErrorTypes[i]);
        }

        return size;
    }

    private ref struct SpanWriter(Span<byte> buffer)
    {
        private readonly Span<byte> _buffer = buffer;

        public int Position { get; private set; }

        public void WriteByte(byte value) => _buffer[Position++] = value;

        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer[Position..], value);
            Position += 4;
        }

        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer[Position..], value);
            Position += 8;
        }

        public void WriteGuid(Guid value)
        {
            value.TryWriteBytes(_buffer[Position..], bigEndian: true, out _);
            Position += 16;
        }

        public void WriteBytes(ReadOnlySpan<byte> value)
        {
            WriteInt32(value.Length);
            value.CopyTo(_buffer[Position..]);
            Position += value.Length;
        }

        public void WriteString(string value)
        {
            int written = Encoding.UTF8.GetBytes(value, _buffer[(Position + 4)..]);
            WriteInt32(written);
            Position += written;
        }
    }
}
