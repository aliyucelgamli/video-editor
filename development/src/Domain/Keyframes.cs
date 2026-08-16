namespace VideoEditor.Domain;

/// <summary>A single keyframe: a value at a point in time (seconds, relative to the owner).</summary>
public class Keyframe
{
    public double Time { get; set; }
    public double Value { get; set; }
    public KeyframeInterpolation Interpolation { get; set; } = KeyframeInterpolation.Linear;
}

/// <summary>All keyframes animating one property (e.g. "opacity", "scaleX", "volume").</summary>
public class KeyframeTrack
{
    public string Property { get; set; } = string.Empty;
    public List<Keyframe> Keyframes { get; set; } = new();

    /// <summary>Independent copy, keyframes included (clip copy/paste).</summary>
    public KeyframeTrack Clone() => new()
    {
        Property = Property,
        Keyframes = Keyframes
            .Select(k => new Keyframe { Time = k.Time, Value = k.Value, Interpolation = k.Interpolation })
            .ToList()
    };
}
