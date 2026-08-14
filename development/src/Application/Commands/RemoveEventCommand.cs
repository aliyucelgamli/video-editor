using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

public class RemoveEventCommand : IEditorCommand
{
    private readonly Track _track;
    private readonly TimelineEvent _event;

    public RemoveEventCommand(Track track, TimelineEvent timelineEvent)
    {
        _track = track;
        _event = timelineEvent;
    }

    public string Description => $"Delete event '{_event.Name}'";

    public void Execute() => _track.Events.Remove(_event);

    public void Undo()
    {
        _track.Events.Add(_event);
        _track.SortEvents();
    }
}
