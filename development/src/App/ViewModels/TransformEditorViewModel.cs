using System.Windows.Media;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Ui;
using VideoEditor.Application.Commands;
using VideoEditor.Application.Editing;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// Backs the visual transform editor: holds the live scale/position of one
/// clip while the gizmo drags it, loads the clip's frame for the stage, and
/// turns the whole editing session into a single undoable command on OK
/// (or rolls it back when the window closes without committing).
/// </summary>
public class TransformEditorViewModel : ObservableObject
{
    private const int MaxStageFrameWidth = 720;

    private readonly TimelineEvent _event;
    private readonly FrameExtractor _extractor;
    private readonly string? _mediaPath;
    private readonly double _sourceTime;
    private readonly Action<IEditorCommand> _run;
    private readonly Action _previewRefresh;
    private readonly double[] _original;
    private readonly RawFrame? _presetFrame;
    private bool _committed;
    private bool _lockAspect = true;

    public TransformEditorViewModel(
        TimelineEvent evt,
        MediaItem? media,
        ProjectSettings settings,
        FrameExtractor extractor,
        double sourceTime,
        Action<IEditorCommand> run,
        Action previewRefresh,
        RawFrame? presetFrame = null)
    {
        _presetFrame = presetFrame;
        _event = evt;
        _extractor = extractor;
        _mediaPath = media?.FilePath;
        _sourceTime = sourceTime;
        _run = run;
        _previewRefresh = previewRefresh;

        var t = evt.Transform;
        _original = new[] { t.ScaleX, t.ScaleY, t.PositionX, t.PositionY };

        ClipName = evt.Name;
        ProjectWidth = settings.Width;
        ProjectHeight = settings.Height;
        SourceLabel = media?.Width is int w && media.Height is int h
            ? $"Source {w}×{h}  •  Project {settings.Width}×{settings.Height}"
            : $"Project {settings.Width}×{settings.Height}";
    }

    public string ClipName { get; }
    public string SourceLabel { get; }
    public int ProjectWidth { get; }
    public int ProjectHeight { get; }

    public double ScaleX => _event.Transform.ScaleX;
    public double ScaleY => _event.Transform.ScaleY;
    public double PositionX => _event.Transform.PositionX;
    public double PositionY => _event.Transform.PositionY;

    /// <summary>Corner drags keep the current aspect ratio while this is set.</summary>
    public bool LockAspect
    {
        get => _lockAspect;
        set => SetProperty(ref _lockAspect, value);
    }

    /// <summary>
    /// Writes all four transform values at once (gizmo drags and typed edits),
    /// refreshing the main preview live. Undo history is untouched until
    /// <see cref="Commit"/>.
    /// </summary>
    public void SetTransform(double scaleX, double scaleY, double positionX, double positionY)
    {
        var t = _event.Transform;
        t.ScaleX = Math.Clamp(scaleX, TransformGizmo.MinScale, TransformGizmo.MaxScale);
        t.ScaleY = Math.Clamp(scaleY, TransformGizmo.MinScale, TransformGizmo.MaxScale);
        t.PositionX = Math.Clamp(positionX, -ProjectWidth, ProjectWidth);
        t.PositionY = Math.Clamp(positionY, -ProjectHeight, ProjectHeight);

        OnPropertyChanged(nameof(ScaleX));
        OnPropertyChanged(nameof(ScaleY));
        OnPropertyChanged(nameof(PositionX));
        OnPropertyChanged(nameof(PositionY));
        _previewRefresh();
    }

    public void Reset() => SetTransform(1, 1, 0, 0);

    /// <summary>Issues ONE undoable command covering the whole editing session.</summary>
    public void Commit()
    {
        _committed = true;
        var t = _event.Transform;
        var changed = Math.Abs(_original[0] - t.ScaleX) > 0.001 ||
                      Math.Abs(_original[1] - t.ScaleY) > 0.001 ||
                      Math.Abs(_original[2] - t.PositionX) > 0.01 ||
                      Math.Abs(_original[3] - t.PositionY) > 0.01;
        if (!changed) return;

        var command = new CompositeCommand($"Transform '{_event.Name}'", new List<IEditorCommand>
        {
            new SetValueCommand<double>("Scale X", _original[0], t.ScaleX, v => t.ScaleX = v),
            new SetValueCommand<double>("Scale Y", _original[1], t.ScaleY, v => t.ScaleY = v),
            new SetValueCommand<double>("Pos X", _original[2], t.PositionX, v => t.PositionX = v),
            new SetValueCommand<double>("Pos Y", _original[3], t.PositionY, v => t.PositionY = v)
        });
        // Rewind to the baseline first so undo lands exactly where editing started.
        command.Undo();
        _run(command);
    }

    /// <summary>Cancel / close without OK: put the clip back the way it was.</summary>
    public void RevertIfUncommitted()
    {
        if (_committed) return;
        SetTransform(_original[0], _original[1], _original[2], _original[3]);
    }

    /// <summary>
    /// Loads the clip's frame for the stage (letterboxed to the project
    /// aspect, like the compositor sees it). Null when ffmpeg or the source
    /// file is unavailable — the stage then shows an outline only.
    /// </summary>
    public async Task<ImageSource?> LoadFrameAsync(CancellationToken cancellationToken = default)
    {
        if (_presetFrame is { } preset)
            return FrameBitmaps.CreateFrozen(preset.Bgra, preset.Width, preset.Height);
        if (_mediaPath is null) return null;

        var (width, height) = FrameSizes.FitWithin(ProjectWidth, ProjectHeight, MaxStageFrameWidth);
        var frame = await _extractor.GetFrameAsync(_mediaPath, _sourceTime, width, height, cancellationToken);
        return frame is null ? null : FrameBitmaps.CreateFrozen(frame.Bgra, frame.Width, frame.Height);
    }
}
