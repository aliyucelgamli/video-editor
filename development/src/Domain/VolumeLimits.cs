namespace VideoEditor.Domain;

/// <summary>Shared audio gain limits: 0% – 200%, default 100%.</summary>
public static class VolumeLimits
{
    public const double Min = 0.0;
    public const double Max = 2.0;
    public const double Default = 1.0;

    public static double Clamp(double value) => Math.Clamp(value, Min, Max);
}
