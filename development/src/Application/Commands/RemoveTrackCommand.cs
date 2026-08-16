using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>
/// Removes a track with everything on it. The clips travel with the command
/// rather than being deleted separately, so one undo puts the whole lane back
/// exactly as it was — including its position among the other lanes.
/// </summary>
public class RemoveTrackCommand : IEditorCommand
{
    private readonly Project _project;
    private readonly Track _track;
    private int _index = -1;

    public RemoveTrackCommand(Project project, Track track)
    {
        _project = project;
        _track = track;
    }

    public string Description => $"Delete track '{_track.Name}'";

    /// <summary>Clips that would be deleted with the lane — the caller warns about these.</summary>
    public int EventCount => _track.Events.Count;

    public void Execute()
    {
        _index = _project.Tracks.IndexOf(_track);
        if (_index >= 0) _project.Tracks.RemoveAt(_index);
    }

    public void Undo()
    {
        if (_index < 0) return;
        _project.Tracks.Insert(Math.Min(_index, _project.Tracks.Count), _track);
    }
}
