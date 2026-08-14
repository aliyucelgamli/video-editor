using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>Moves an event to a new start time, optionally onto another track.</summary>
public class MoveEventCommand : IEditorCommand
{
    private readonly TimelineEvent _event;
    private readonly Track _fromTrack;
    private readonly Track _toTrack;
    private readonly double _newStart;
    private readonly double _oldStart;

    public MoveEventCommand(TimelineEvent timelineEvent, Track fromTrack, Track toTrack, double newStart)
    {
        _event = timelineEvent;
        _fromTrack = fromTrack;
        _toTrack = toTrack;
        _newStart = newStart;
        _oldStart = timelineEvent.Start;
    }

    public string Description => $"Move event '{_event.Name}'";

    public void Execute()
    {
        _fromTrack.Events.Remove(_event);
        _event.Start = _newStart;
        _toTrack.Events.Add(_event);
        _toTrack.SortEvents();
    }

    public void Undo()
    {
        _toTrack.Events.Remove(_event);
        _event.Start = _oldStart;
        _fromTrack.Events.Add(_event);
        _fromTrack.SortEvents();
    }
}
