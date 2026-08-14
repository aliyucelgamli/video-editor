namespace VideoEditor.Domain.Effects;

/// <summary>
/// One user-adjustable parameter of an effect (e.g. blur radius).
/// Values are always doubles; Min/Max drive the UI slider.
/// </summary>
public class EffectParameterDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public double Min { get; set; }
    public double Max { get; set; } = 1.0;
    public double Default { get; set; }

    /// <summary>Optional unit shown next to the value (e.g. "px", "%", "x").</summary>
    public string? Unit { get; set; }

    public double Clamp(double value) => Math.Clamp(value, Min, Max);
}
