using System.Buffers.Binary;
using System.Text;

namespace Kairos;

/// <summary>
/// Stable binary form of a history event (the same bytes the hash chain covers). Stores persist this as the event body;
/// decoding treats its input as untrusted and throws <see cref="FormatException"/> on anything malformed.
/// </summary>
public static class HistoryEventCodec
{
    /// <summary>Encodes the event body (excluding PrevHash/Hash, which are stored beside it).</summary>
    public static byte[] Encode(HistoryEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        byte[] buffer = new byte[EventEncoder.MaxSize(e)];
        int length = EventEncoder.Write(e, buffer);
        return length == buffer.Length ? buffer : buffer.AsSpan(0, length).ToArray();
    }

    public static HistoryEvent Decode(ReadOnlySpan<byte> body, byte[] prevHash, byte[] hash)
    {
        try
        {
            return DecodeCore(body, prevHash, hash);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            // Out-of-range timestamps/durations and similar: still just malformed input.
            throw new FormatException("Malformed history event.", ex);
        }
    }

    private static HistoryEvent DecodeCore(ReadOnlySpan<byte> body, byte[] prevHash, byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(prevHash);
        ArgumentNullException.ThrowIfNull(hash);
        var r = new Reader(body);

        if (r.ReadByte() != EventEncoder.EncodingVersion)
        {
            throw new FormatException("Unsupported history event encoding version.");
        }

        var type = (HistoryEventType)r.ReadByte();
        Guid tenant = r.ReadGuid(), ns = r.ReadGuid(), run = r.ReadGuid();
        long seq = r.ReadInt64();
        var ts = new DateTimeOffset(r.ReadInt64(), TimeSpan.Zero);

        HistoryEvent e = type switch
        {
            HistoryEventType.WorkflowStarted => ReadStarted(ref r, tenant, ns, run, seq, ts),
            HistoryEventType.WorkflowContinued => new WorkflowContinued(tenant, ns, run, seq, ts, r.ReadGuid(), r.ReadBytes()),
            HistoryEventType.PatchMarker => new PatchMarker(tenant, ns, run, seq, ts, r.ReadString()),
            HistoryEventType.ActivityScheduled => ReadScheduled(ref r, tenant, ns, run, seq, ts),
            HistoryEventType.ActivityCompleted => ReadCompleted(ref r, tenant, ns, run, seq, ts),
            HistoryEventType.ActivityFailed => ReadFailed(ref r, tenant, ns, run, seq, ts),
            HistoryEventType.TimerStarted => new TimerStarted(tenant, ns, run, seq, ts, r.ReadInt32(), TimeSpan.FromTicks(r.ReadInt64())),
            HistoryEventType.RunTerminated => new RunTerminated(tenant, ns, run, seq, ts, r.ReadString()),
            HistoryEventType.WorkflowTaskCompleted => new WorkflowTaskCompleted(tenant, ns, run, seq, ts),
            HistoryEventType.TimerFired => new TimerFired(tenant, ns, run, seq, ts, r.ReadInt32()),
            HistoryEventType.SignalReceived => ReadSignal(ref r, tenant, ns, run, seq, ts),
            HistoryEventType.WorkflowCompleted => new WorkflowCompleted(tenant, ns, run, seq, ts, r.ReadBytes()),
            HistoryEventType.WorkflowFailed => new WorkflowFailed(tenant, ns, run, seq, ts, r.ReadString(), r.ReadString()),
            _ => throw new FormatException($"Unknown history event type {(byte)type}."),
        };

        if (!r.AtEnd)
        {
            throw new FormatException("Trailing bytes after history event.");
        }

        return e with { PrevHash = prevHash, Hash = hash };
    }

    private static SignalReceived ReadSignal(ref Reader r, Guid tenant, Guid ns, Guid run, long seq, DateTimeOffset ts)
    {
        string name = r.ReadString();
        byte[] payload = r.ReadBytes();
        string id = "";
        string trace = "";
        if (!r.AtEnd)
        {
            id = r.ReadString();
            if (!r.AtEnd)
            {
                trace = ReadTrace(ref r);
            }
            else if (id.Length == 0)
            {
                throw new FormatException("Non-canonical signal id."); // an empty id is encoded by leaving it out, unless a trace context follows
            }
        }

        return new SignalReceived(tenant, ns, run, seq, ts, name, payload) { SignalId = id, TraceContext = trace };
    }

    private static WorkflowStarted ReadStarted(ref Reader r, Guid tenant, Guid ns, Guid run, long seq, DateTimeOffset ts)
    {
        string type = r.ReadString();
        string build = r.ReadString();
        byte[] input = r.ReadBytes();
        Guid previous = r.ReadGuid();
        string trace = "";
        string startedBy = "";
        if (!r.AtEnd)
        {
            trace = r.ReadString();
            if (!r.AtEnd)
            {
                startedBy = r.ReadString();
                if (startedBy.Length == 0 || startedBy.Length > WorkflowStarted.MaxStartedByLength)
                {
                    throw new FormatException("Invalid started-by."); // an empty one is encoded by leaving it out
                }
            }
            else if (trace.Length == 0)
            {
                throw new FormatException("Non-canonical trace context."); // an empty one is encoded by leaving it out, unless a started-by follows
            }
        }

        return new WorkflowStarted(tenant, ns, run, seq, ts, type, build, input) { PreviousRunId = previous, TraceContext = trace, StartedBy = startedBy };
    }

    private static ActivityScheduled ReadScheduled(ref Reader r, Guid tenant, Guid ns, Guid run, long seq, DateTimeOffset ts)
    {
        int id = r.ReadInt32();
        string type = r.ReadString();
        byte[] input = r.ReadBytes();
        int attempts = r.ReadInt32();
        var initial = TimeSpan.FromTicks(r.ReadInt64());
        double coefficient = BitConverter.Int64BitsToDouble(r.ReadInt64());
        var max = TimeSpan.FromTicks(r.ReadInt64());
        int count = r.ReadInt32();
        if (count < 0 || count > r.Remaining)
        {
            throw new FormatException("Invalid retry policy.");
        }

        var errors = new string[count];
        for (int i = 0; i < count; i++)
        {
            errors[i] = r.ReadString();
        }

        var policy = new RetryPolicy
        {
            MaximumAttempts = attempts,
            InitialInterval = initial,
            BackoffCoefficient = coefficient,
            MaximumInterval = max,
            NonRetryableErrorTypes = errors,
        };
        return new ActivityScheduled(tenant, ns, run, seq, ts, id, type, input, policy) { TraceContext = r.AtEnd ? "" : ReadTrace(ref r) };
    }

    private static ActivityCompleted ReadCompleted(ref Reader r, Guid tenant, Guid ns, Guid run, long seq, DateTimeOffset ts)
    {
        int id = r.ReadInt32();
        byte[] result = r.ReadBytes();
        return new ActivityCompleted(tenant, ns, run, seq, ts, id, result) { Usage = r.AtEnd ? null : ReadUsage(ref r) };
    }

    private static ActivityFailed ReadFailed(ref Reader r, Guid tenant, Guid ns, Guid run, long seq, DateTimeOffset ts)
    {
        int id = r.ReadInt32(), attempt = r.ReadInt32();
        string type = r.ReadString(), message = r.ReadString();
        bool retry = r.ReadByte() != 0;
        return new ActivityFailed(tenant, ns, run, seq, ts, id, attempt, type, message, retry) { Usage = r.AtEnd ? null : ReadUsage(ref r) };
    }

    private static UsageRecord ReadUsage(ref Reader r)
    {
        long input = r.ReadInt64(), output = r.ReadInt64(), cached = r.ReadInt64();
        string model = r.ReadString();
        return input < 0 || output < 0 || cached < 0 || cached > input || model.Length > UsageRecord.MaxModelLength
            ? throw new FormatException("Invalid usage record.")
            : new UsageRecord(input, output, cached, model);
    }

    private static string ReadTrace(ref Reader r)
    {
        string trace = r.ReadString();
        if (trace.Length == 0)
        {
            throw new FormatException("Non-canonical trace context."); // an empty one is encoded by leaving it out
        }

        return trace;
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public readonly bool AtEnd => _pos == _data.Length;

        public readonly int Remaining => _data.Length - _pos;

        public byte ReadByte()
        {
            Need(1);
            return _data[_pos++];
        }

        public int ReadInt32()
        {
            Need(4);
            int v = BinaryPrimitives.ReadInt32LittleEndian(_data[_pos..]);
            _pos += 4;
            return v;
        }

        public long ReadInt64()
        {
            Need(8);
            long v = BinaryPrimitives.ReadInt64LittleEndian(_data[_pos..]);
            _pos += 8;
            return v;
        }

        public Guid ReadGuid()
        {
            Need(16);
            var g = new Guid(_data.Slice(_pos, 16), bigEndian: true);
            _pos += 16;
            return g;
        }

        public byte[] ReadBytes()
        {
            int n = ReadInt32();
            Need(n);
            byte[] v = _data.Slice(_pos, n).ToArray();
            _pos += n;
            return v;
        }

        public string ReadString()
        {
            int n = ReadInt32();
            Need(n);
            try
            {
                string v = new UTF8Encoding(false, true).GetString(_data.Slice(_pos, n));
                _pos += n;
                return v;
            }
            catch (ArgumentException ex)
            {
                throw new FormatException("Invalid UTF-8 in history event.", ex);
            }
        }

        private readonly void Need(int n)
        {
            if (n < 0 || n > _data.Length - _pos)
            {
                throw new FormatException("Truncated history event.");
            }
        }
    }
}
