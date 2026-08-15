namespace VideoEditor.Domain;

/// <summary>
/// Standard easing functions (easings.net formulas). Used by fades, the
/// audio filter mapping and later by keyframe interpolation. Input is
/// clamped to 0..1; Back variants intentionally overshoot outside 0..1 —
/// consumers clamp the result where overshoot makes no sense (opacity).
/// </summary>
public static class Easing
{
    private const double BackC1 = 1.70158;
    private const double BackC2 = BackC1 * 1.525;
    private const double BackC3 = BackC1 + 1;

    public static double Evaluate(EasingType type, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return type switch
        {
            EasingType.InSine => 1 - Math.Cos(t * Math.PI / 2),
            EasingType.OutSine => Math.Sin(t * Math.PI / 2),
            EasingType.InOutSine => -(Math.Cos(Math.PI * t) - 1) / 2,
            EasingType.InQuad => t * t,
            EasingType.OutQuad => 1 - (1 - t) * (1 - t),
            EasingType.InOutQuad => t < 0.5
                ? 2 * t * t
                : 1 - Math.Pow(-2 * t + 2, 2) / 2,
            EasingType.InCubic => t * t * t,
            EasingType.OutCubic => 1 - Math.Pow(1 - t, 3),
            EasingType.InOutCubic => t < 0.5
                ? 4 * t * t * t
                : 1 - Math.Pow(-2 * t + 2, 3) / 2,
            EasingType.InBack => BackC3 * t * t * t - BackC1 * t * t,
            EasingType.OutBack => 1 + BackC3 * Math.Pow(t - 1, 3) + BackC1 * Math.Pow(t - 1, 2),
            EasingType.InOutBack => t < 0.5
                ? Math.Pow(2 * t, 2) * ((BackC2 + 1) * 2 * t - BackC2) / 2
                : (Math.Pow(2 * t - 2, 2) * ((BackC2 + 1) * (t * 2 - 2) + BackC2) + 2) / 2,
            _ => t
        };
    }
}
