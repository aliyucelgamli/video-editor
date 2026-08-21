namespace VideoEditor.Domain.Sound;

/// <summary>
/// One piece of a source audio file, expressed in SOURCE time. Editing a sound
/// clip never rewrites the file on disk: a session is an ordered list of these
/// windows plus per-piece level and fades, exactly like a timeline event.
/// </summary>
public sealed class SoundSegment
{
    /// <summary>A fade shorter than this is treated as no fade at all.</summary>
    public const double MinFade = 0.001;

    public Guid Id { get; set; } = Guid.NewGuid();

    public double SourceIn { get; set; }
    public double SourceOut { get; set; }

    /// <summary>Linear level multiplier; shares the clip volume range (0–200%).</summary>
    public double Gain { get; set; } = VolumeLimits.Default;

    public double FadeIn { get; set; }
    public double FadeOut { get; set; }
    public EasingType FadeInEasing { get; set; } = EasingType.Linear;
    public EasingType FadeOutEasing { get; set; } = EasingType.Linear;
    public bool Muted { get; set; }

    public double Duration => Math.Max(0, SourceOut - SourceIn);

    /// <summary>Effective level: muted pieces are silent, everything else is clamped.</summary>
    public double EffectiveGain => Muted ? 0 : VolumeLimits.Clamp(Gain);

    /// <summary>Keeps the two fades inside the piece and out of each other's way.</summary>
    public void ClampFades()
    {
        var duration = Duration;
        FadeIn = Math.Clamp(FadeIn, 0, duration);
        FadeOut = Math.Clamp(FadeOut, 0, Math.Max(0, duration - FadeIn));
        if (FadeIn < MinFade) FadeIn = 0;
        if (FadeOut < MinFade) FadeOut = 0;
    }

    /// <summary>
    /// Value of the fade envelope at <paramref name="localTime"/> seconds into
    /// the piece (0–1, gain excluded) — the same curve the exported afade draws.
    /// </summary>
    public double FadeFactorAt(double localTime)
    {
        var factor = 1.0;
        if (FadeIn > 0 && localTime < FadeIn)
            factor = Math.Clamp(Easing.Evaluate(FadeInEasing, localTime / FadeIn), 0, 1);

        var fromEnd = Duration - localTime;
        if (FadeOut > 0 && fromEnd < FadeOut)
            factor = Math.Min(factor, Math.Clamp(Easing.Evaluate(FadeOutEasing, fromEnd / FadeOut), 0, 1));

        return factor;
    }

    /// <summary>
    /// Identical copy, identity included — sessions are snapshotted for undo,
    /// so a copy has to stay the same piece (unlike <c>TimelineEvent.Clone</c>).
    /// </summary>
    public SoundSegment Copy() => new()
    {
        Id = Id,
        SourceIn = SourceIn,
        SourceOut = SourceOut,
        Gain = Gain,
        FadeIn = FadeIn,
        FadeOut = FadeOut,
        FadeInEasing = FadeInEasing,
        FadeOutEasing = FadeOutEasing,
        Muted = Muted
    };
}
