using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>
/// Splits an event at a timeline position. Non-destructive: only the
/// sourceIn/sourceOut references change; the source media file is untouched.
/// Splitting linked audio+video together is done by wrapping two of these
/// in a CompositeCommand (later step).
/// </summary>
public class SplitEventCommand : IEditorCommand
{
    private readonly Track _track;
    private readonly TimelineEvent _first;
    private readonly double _splitTime;

    private readonly double _originalDuration;
    private readonly double _originalSourceOut;
    private readonly double _originalFadeOut;
    private readonly EasingType _originalFadeOutEasing;

    private TimelineEvent? _second;

    public SplitEventCommand(Track track, TimelineEvent timelineEvent, double splitTime)
    {
        if (splitTime <= timelineEvent.Start || splitTime >= timelineEvent.End)
            throw new ArgumentOutOfRangeException(nameof(splitTime), "Split time must fall inside the event.");

        _track = track;
        _first = timelineEvent;
        _splitTime = splitTime;

        _originalDuration = timelineEvent.Duration;
        _originalSourceOut = timelineEvent.SourceOut;
        _originalFadeOut = timelineEvent.FadeOutDuration;
        _originalFadeOutEasing = timelineEvent.FadeOutEasing;
    }

    public string Description => $"Split event '{_first.Name}'";

    /// <summary>The right-hand event created by the split (available after Execute).</summary>
    public TimelineEvent? SecondEvent => _second;

    public void Execute()
    {
        var offset = _splitTime - _first.Start;
        var sourceSplit = _first.SourceIn + offset * _first.PlaybackRate;

        _second ??= new TimelineEvent
        {
            MediaId = _first.MediaId,
            Name = _first.Name,
            Start = _splitTime,
            Duration = _originalDuration - offset,
            SourceIn = sourceSplit,
            SourceOut = _originalSourceOut,
            PlaybackRate = _first.PlaybackRate,
            FadeOutDuration = _originalFadeOut,
            FadeOutEasing = _originalFadeOutEasing,
            FadeInEasing = _first.FadeInEasing,
            Volume = _first.Volume,
            Opacity = _first.Opacity,
            Muted = _first.Muted,
            Transform = _first.Transform.Clone(),
            // Effect/keyframe copying and linked-event handling arrive in a later step.
            LinkedEventId = null
        };

        _first.Duration = offset;
        _first.SourceOut = sourceSplit;
        _first.FadeOutDuration = 0;

        _track.Events.Add(_second);
        _track.SortEvents();
    }

    public void Undo()
    {
        if (_second != null) _track.Events.Remove(_second);
        _first.Duration = _originalDuration;
        _first.SourceOut = _originalSourceOut;
        _first.FadeOutDuration = _originalFadeOut;
        _first.FadeOutEasing = _originalFadeOutEasing;
    }
}
