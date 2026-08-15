using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>
/// Reorders a track (dragging a lane header up or down). Lane order decides
/// the stack when clips share a layer — the top lane sits at the bottom.
/// </summary>
public class MoveTrackCommand : IEditorCommand
{
    private readonly Project _project;
    private readonly Track _track;
    private readonly int _newIndex;
    private readonly int _oldIndex;

    public MoveTrackCommand(Project project, Track track, int newIndex)
    {
        _project = project;
        _track = track;
        _oldIndex = project.Tracks.IndexOf(track);
        _newIndex = Math.Clamp(newIndex, 0, Math.Max(0, project.Tracks.Count - 1));
    }

    public string Description => $"Move track '{_track.Name}'";

    public bool ChangesOrder => _oldIndex >= 0 && _oldIndex != _newIndex;

    public void Execute() => MoveTo(_newIndex);

    public void Undo() => MoveTo(_oldIndex);

    private void MoveTo(int index)
    {
        if (_oldIndex < 0) return;
        if (!_project.Tracks.Remove(_track)) return;
        _project.Tracks.Insert(Math.Clamp(index, 0, _project.Tracks.Count), _track);
    }
}
