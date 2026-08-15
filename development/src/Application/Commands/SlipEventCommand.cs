using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>
/// Slips an event (Alt+drag): the timeline position and duration stay fixed
/// while the visible source range slides inside the media.
/// </summary>
public class SlipEventCommand : IEditorCommand
{
    private readonly TimelineEvent _event;
    private readonly double _newSourceIn;
    private readonly double _newSourceOut;
    private readonly double _oldSourceIn;
    private readonly double _oldSourceOut;

    public SlipEventCommand(TimelineEvent timelineEvent, double newSourceIn, double newSourceOut)
    {
        _event = timelineEvent;
        _newSourceIn = newSourceIn;
        _newSourceOut = newSourceOut;
        _oldSourceIn = timelineEvent.SourceIn;
        _oldSourceOut = timelineEvent.SourceOut;
    }

    public string Description => $"Slip '{_event.Name}'";

    public void Execute()
    {
        _event.SourceIn = _newSourceIn;
        _event.SourceOut = _newSourceOut;
    }

    public void Undo()
    {
        _event.SourceIn = _oldSourceIn;
        _event.SourceOut = _oldSourceOut;
    }
}
