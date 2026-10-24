namespace Kairos.Testing;

/// <summary>Manually advanced clock. Timers are deliberately unsupported: nothing in Core may depend on real or hidden time.</summary>
public sealed class VirtualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public VirtualTimeProvider(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        _now += by;
    }

    public void AdvanceTo(DateTimeOffset to)
    {
        if (to > _now)
        {
            _now = to;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        throw new NotSupportedException("VirtualTimeProvider does not create timers; drive time explicitly.");
}
