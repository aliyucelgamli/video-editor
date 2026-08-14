using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>Toggles a boolean track flag (mute/solo) as an undoable operation.</summary>
public class SetTrackFlagCommand : IEditorCommand
{
    public enum TrackFlag { Muted, Solo }

    private readonly Track _track;
    private readonly TrackFlag _flag;
    private readonly bool _newValue;
    private readonly bool _oldValue;

    public SetTrackFlagCommand(Track track, TrackFlag flag, bool value)
    {
        _track = track;
        _flag = flag;
        _newValue = value;
        _oldValue = flag == TrackFlag.Muted ? track.Muted : track.Solo;
    }

    public string Description => $"{(_newValue ? "Enable" : "Disable")} {_flag} on '{_track.Name}'";

    public void Execute() => Apply(_newValue);

    public void Undo() => Apply(_oldValue);

    private void Apply(bool value)
    {
        if (_flag == TrackFlag.Muted) _track.Muted = value;
        else _track.Solo = value;
    }
}
