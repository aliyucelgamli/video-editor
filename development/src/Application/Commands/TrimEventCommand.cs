using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>
/// Trims an event edge without changing its playback rate (plain edge drag):
/// the timeline span and the visible source range shrink or grow together.
/// Non-destructive — only the event's references change.
/// </summary>
public class TrimEventCommand : IEditorCommand
{
    public const double MinDuration = 0.1;

    private readonly TimelineEvent _event;
    private readonly double _newStart;
    private readonly double _newDuration;
    private readonly double _newSourceIn;
    private readonly double _newSourceOut;

    private readonly double _oldStart;
    private readonly double _oldDuration;
    private readonly double _oldSourceIn;
    private readonly double _oldSourceOut;

    public TrimEventCommand(
        TimelineEvent timelineEvent,
        double newStart, double newDuration, double newSourceIn, double newSourceOut)
    {
        _event = timelineEvent;
        _newStart = newStart;
        _newDuration = Math.Max(MinDuration, newDuration);
        _newSourceIn = newSourceIn;
        _newSourceOut = newSourceOut;

        _oldStart = timelineEvent.Start;
        _oldDuration = timelineEvent.Duration;
        _oldSourceIn = timelineEvent.SourceIn;
        _oldSourceOut = timelineEvent.SourceOut;
    }

    public string Description => $"Trim '{_event.Name}'";

    public void Execute()
    {
        _event.Start = _newStart;
        _event.Duration = _newDuration;
        _event.SourceIn = _newSourceIn;
        _event.SourceOut = _newSourceOut;
    }

    public void Undo()
    {
        _event.Start = _oldStart;
        _event.Duration = _oldDuration;
        _event.SourceIn = _oldSourceIn;
        _event.SourceOut = _oldSourceOut;
    }
}
