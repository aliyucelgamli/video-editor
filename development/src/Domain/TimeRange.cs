namespace VideoEditor.Domain;

/// <summary>A [start, end) span on the timeline, in seconds.</summary>
public class TimeRange
{
    public double Start { get; set; }
    public double End { get; set; }

    public double Duration => Math.Max(0, End - Start);

    /// <summary>Returns a copy with Start ≤ End and both clamped to ≥ 0.</summary>
    public TimeRange Normalized()
    {
        var a = Math.Max(0, Math.Min(Start, End));
        var b = Math.Max(0, Math.Max(Start, End));
        return new TimeRange { Start = a, End = b };
    }

    public TimeRange Clone() => new() { Start = Start, End = End };
}
