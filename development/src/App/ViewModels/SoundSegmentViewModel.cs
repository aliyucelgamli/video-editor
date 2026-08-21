using VideoEditor.App.Mvvm;
using VideoEditor.App.Ui;
using VideoEditor.Domain;
using VideoEditor.Domain.Sound;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// One piece of a sound clip, as the editor's segment list and its level/fade
/// controls see it. A live wrapper: setters write straight into the model for an
/// instant repaint and report through <c>edited</c>, following the app's slider
/// pattern (live value now, one undo entry on release).
/// </summary>
public sealed class SoundSegmentViewModel : ObservableObject
{
    private readonly SoundSegment _segment;
    private readonly Action _edited;

    public SoundSegmentViewModel(SoundSegment segment, int index, double startSeconds, Action edited)
    {
        _segment = segment;
        _edited = edited;
        Index = index;
        StartSeconds = startSeconds;
    }

    public Guid Id => _segment.Id;

    /// <summary>Zero-based position in the running order.</summary>
    public int Index { get; }

    public string NumberLabel => $"{Index + 1}";

    /// <summary>Header of the level/fade panel while this piece is being edited.</summary>
    public string TitleLabel => $"PIECE {Index + 1}";

    /// <summary>Where this piece starts in the edited result.</summary>
    public double StartSeconds { get; }

    public double DurationSeconds => _segment.Duration;

    /// <summary>Span inside the source file — what part of the original this is.</summary>
    public string SourceRangeLabel =>
        $"{TimeText.Compact(_segment.SourceIn)} – {TimeText.Compact(_segment.SourceOut)}";

    public string DurationLabel => $"{DurationSeconds:0.##}s";

    /// <summary>Level as a percentage, matching the clip and track volume sliders.</summary>
    public double GainPercent
    {
        get => Math.Round(VolumeLimits.Clamp(_segment.Gain) * 100);
        set
        {
            var clamped = VolumeLimits.Clamp(value / 100.0);
            if (Math.Abs(_segment.Gain - clamped) < 0.0001) return;
            _segment.Gain = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GainLabel));
            _edited();
        }
    }

    /// <summary>Percent plus the decibel equivalent, which is how audio people think.</summary>
    public string GainLabel
    {
        get
        {
            var gain = VolumeLimits.Clamp(_segment.Gain);
            if (gain <= 0.0001) return "silent";
            return $"{gain * 100:0}%  ({20 * Math.Log10(gain):+0.0;-0.0;0.0} dB)";
        }
    }

    /// <summary>Longest fade this piece can hold.</summary>
    public double FadeLimit => Math.Max(0.01, DurationSeconds);

    public double FadeIn
    {
        get => _segment.FadeIn;
        set => SetFade(value, isFadeIn: true);
    }

    public double FadeOut
    {
        get => _segment.FadeOut;
        set => SetFade(value, isFadeIn: false);
    }

    /// <summary>
    /// Fade length as a fraction of the piece (0–1). The sliders bind to this
    /// rather than to seconds so their Maximum is a constant: a bound Maximum
    /// would clamp an incoming Value whenever the two arrived out of order,
    /// silently shortening the fade on switching pieces.
    /// </summary>
    public double FadeInFraction
    {
        get => DurationSeconds <= 0 ? 0 : Math.Clamp(_segment.FadeIn / DurationSeconds, 0, 1);
        set => SetFade(value * DurationSeconds, isFadeIn: true);
    }

    public double FadeOutFraction
    {
        get => DurationSeconds <= 0 ? 0 : Math.Clamp(_segment.FadeOut / DurationSeconds, 0, 1);
        set => SetFade(value * DurationSeconds, isFadeIn: false);
    }

    public string FadeInLabel => FadeLabelFor(_segment.FadeIn);
    public string FadeOutLabel => FadeLabelFor(_segment.FadeOut);

    public int FadeInEasingIndex
    {
        get => EasingOptions.IndexOf(_segment.FadeInEasing);
        set
        {
            var easing = EasingOptions.At(value);
            if (_segment.FadeInEasing == easing) return;
            _segment.FadeInEasing = easing;
            OnPropertyChanged();
            _edited();
        }
    }

    public int FadeOutEasingIndex
    {
        get => EasingOptions.IndexOf(_segment.FadeOutEasing);
        set
        {
            var easing = EasingOptions.At(value);
            if (_segment.FadeOutEasing == easing) return;
            _segment.FadeOutEasing = easing;
            OnPropertyChanged();
            _edited();
        }
    }

    public bool Muted
    {
        get => _segment.Muted;
        set
        {
            if (_segment.Muted == value) return;
            _segment.Muted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MuteLabel));
            _edited();
        }
    }

    public string MuteLabel => _segment.Muted ? "Muted" : "Audible";

    public string ToolTip =>
        $"Piece {NumberLabel} — {DurationLabel} of the source ({SourceRangeLabel})\n" +
        $"Level {GainLabel}" + (_segment.Muted ? " · muted" : string.Empty);

    /// <summary>Restores the piece to full level with no fades.</summary>
    public void ResetLevels()
    {
        _segment.Gain = VolumeLimits.Default;
        _segment.FadeIn = 0;
        _segment.FadeOut = 0;
        _segment.Muted = false;
        RaiseAll();
        _edited();
    }

    private void SetFade(double seconds, bool isFadeIn)
    {
        var requested = Math.Clamp(seconds, 0, FadeLimit);
        if (isFadeIn) _segment.FadeIn = requested;
        else _segment.FadeOut = requested;
        _segment.ClampFades();

        // ClampFades can shorten the other fade to make room, so both refresh.
        RaiseFades();
        _edited();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(GainPercent));
        OnPropertyChanged(nameof(GainLabel));
        RaiseFades();
        OnPropertyChanged(nameof(Muted));
        OnPropertyChanged(nameof(MuteLabel));
    }

    private void RaiseFades()
    {
        OnPropertyChanged(nameof(FadeIn));
        OnPropertyChanged(nameof(FadeOut));
        OnPropertyChanged(nameof(FadeInFraction));
        OnPropertyChanged(nameof(FadeOutFraction));
        OnPropertyChanged(nameof(FadeInLabel));
        OnPropertyChanged(nameof(FadeOutLabel));
    }

    private static string FadeLabelFor(double seconds) => seconds <= 0 ? "off" : $"{seconds:0.##}s";
}
