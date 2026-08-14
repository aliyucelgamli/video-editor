using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>
/// Time-stretches an event (Shift + edge drag): the timeline duration changes
/// while the same source range keeps playing, so the playback rate adjusts —
/// shorter = faster, longer = slower. Works for video and audio events;
/// non-destructive (SourceIn/SourceOut never change).
/// </summary>
public class StretchEventCommand : IEditorCommand
{
    public const double MinDuration = 0.1;

    private readonly TimelineEvent _event;
    private readonly double _newStart;
    private readonly double _newDuration;
    private readonly double _newRate;
    private readonly double _oldStart;
    private readonly double _oldDuration;
    private readonly double _oldRate;

    public StretchEventCommand(TimelineEvent evt, double newStart, double newDuration)
    {
        _event = evt;
        _oldStart = evt.Start;
        _oldDuration = evt.Duration;
        _oldRate = evt.PlaybackRate;

        _newStart = Math.Max(0, newStart);
        _newDuration = Math.Max(MinDuration, newDuration);

        var sourceSpan = Math.Max(0, evt.SourceOut - evt.SourceIn);
        _newRate = sourceSpan <= 0 ? evt.PlaybackRate : sourceSpan / _newDuration;
    }

    public string Description => $"Stretch '{_event.Name}' to {_newRate:0.##}x";

    /// <summary>The playback rate this stretch results in (for status messages).</summary>
    public double NewRate => _newRate;

    public void Execute() => Apply(_newStart, _newDuration, _newRate);

    public void Undo() => Apply(_oldStart, _oldDuration, _oldRate);

    private void Apply(double start, double duration, double rate)
    {
        _event.Start = start;
        _event.Duration = duration;
        _event.PlaybackRate = rate;
    }
}
