using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

public class AddEventCommand : IEditorCommand
{
    private readonly Track _track;
    private readonly TimelineEvent _event;

    public AddEventCommand(Track track, TimelineEvent timelineEvent)
    {
        _track = track;
        _event = timelineEvent;
    }

    public string Description => $"Add event '{_event.Name}'";

    public void Execute()
    {
        _track.Events.Add(_event);
        _track.SortEvents();
    }

    public void Undo() => _track.Events.Remove(_event);
}
