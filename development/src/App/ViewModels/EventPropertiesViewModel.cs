using VideoEditor.App.Mvvm;
using VideoEditor.Application.Commands;
using VideoEditor.Domain;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// Backs the Event Properties window: size/position (transform), playback
/// speed, volume, opacity, fades and read-only info about one clip. Sliders
/// write the model live (instant preview); one undoable command is issued per
/// drag via BeginEdit/EndEdit(key).
/// </summary>
public class EventPropertiesViewModel : ObservableObject
{
    private readonly TimelineEvent _event;
    private readonly TimelineEvent? _volumeTarget; // audio event itself or the linked audio partner
    private readonly ProjectSettings _settings;
    private readonly Action<IEditorCommand> _run;
    private readonly Action _previewRefresh;

    private readonly Dictionary<string, double[]> _editBaselines = new();

    public EventPropertiesViewModel(
        TimelineEvent evt,
        Track track,
        MediaItem? media,
        TimelineEvent? volumeTarget,
        ProjectSettings settings,
        Action<IEditorCommand> run,
        Action previewRefresh)
    {
        _event = evt;
        _volumeTarget = volumeTarget;
        _settings = settings;
        _run = run;
        _previewRefresh = previewRefresh;

        ClipName = evt.Name;
        IsVisual = track.Type != TrackType.Audio && media?.Type != MediaType.Audio;
        HasVolume = volumeTarget != null;
        FileLabel = media?.FilePath ?? "(no source file)";

        IsText = evt.Text != null;
        IsAudioClip = !IsVisual;
        // Text is generated, so it has no source footage to speed up or slow down.
        HasPlaybackSpeed = !IsText && media != null;
        TypeTitle = IsText ? "TEXT"
            : IsAudioClip ? "AUDIO CLIP"
            : media?.Type == MediaType.Image ? "IMAGE"
            : "VIDEO CLIP";

        RaiseLayerCommand = new RelayCommand(() => NudgeLayer(+1));
        LowerLayerCommand = new RelayCommand(() => NudgeLayer(-1));

        var mediaInfo = media?.Width is int w && media.Height is int h ? $"  •  {w}×{h}" : string.Empty;
        InfoLabel = $"Timeline {evt.Start:0.##}s – {evt.End:0.##}s ({evt.Duration:0.##}s)" +
                    $"  •  Source {evt.SourceIn:0.##}s – {evt.SourceOut:0.##}s{mediaInfo}";

        ResetTransformCommand = new RelayCommand(ResetTransform, () => IsVisual);
    }

    public string ClipName { get; }
    public string FileLabel { get; }
    public string InfoLabel { get; }
    public bool IsVisual { get; }
    public bool HasVolume { get; }
    public bool IsText { get; }
    public bool IsAudioClip { get; }
    public bool HasPlaybackSpeed { get; }

    /// <summary>Header of the type-specific half ("VIDEO CLIP", "TEXT"…).</summary>
    public string TypeTitle { get; }

    public RelayCommand RaiseLayerCommand { get; }
    public RelayCommand LowerLayerCommand { get; }
    public double MaxPositionX => _settings.Width;
    public double MaxPositionY => _settings.Height;
    public double MaxPositionXNegative => -_settings.Width;
    public double MaxPositionYNegative => -_settings.Height;
    public RelayCommand ResetTransformCommand { get; }

    // ---------- Size & position (visual clips) ----------

    public double ScalePercent
    {
        get => Math.Round(_event.Transform.ScaleX * 100);
        set
        {
            var scale = Math.Clamp(value / 100.0, 0.1, 4.0);
            _event.Transform.ScaleX = scale;
            _event.Transform.ScaleY = scale; // uniform scaling from the quick panel
            Changed(nameof(ScalePercent), nameof(ScaleLabel));
        }
    }

    public string ScaleLabel => $"{ScalePercent:0}%";

    public double PositionX
    {
        get => Math.Round(_event.Transform.PositionX);
        set { _event.Transform.PositionX = value; Changed(nameof(PositionX), nameof(PositionXLabel)); }
    }

    public string PositionXLabel => $"{PositionX:0} px";

    public double PositionY
    {
        get => Math.Round(_event.Transform.PositionY);
        set { _event.Transform.PositionY = value; Changed(nameof(PositionY), nameof(PositionYLabel)); }
    }

    public string PositionYLabel => $"{PositionY:0} px";

    // ---------- Layer (compositing order) ----------

    public string LayerLabel => IsVisual
        ? _event.Layer.ToString()
        : "n/a";

    private void NudgeLayer(int delta)
    {
        if (!IsVisual) return;
        var evt = _event;
        var target = Domain.Layers.Clamp(evt.Layer + delta);
        if (target == evt.Layer) return;

        _run(new SetValueCommand<int>(
            $"Set layer of '{evt.Name}'", evt.Layer, target, v => evt.Layer = v));
        Changed(nameof(LayerLabel));
    }

    private void ResetTransform()
    {
        BeginEdit("scale");
        _event.Transform.ScaleX = 1;
        _event.Transform.ScaleY = 1;
        _event.Transform.PositionX = 0;
        _event.Transform.PositionY = 0;
        EndEdit("scale");
        Changed(nameof(ScalePercent), nameof(ScaleLabel));
        Changed(nameof(PositionX), nameof(PositionXLabel));
        Changed(nameof(PositionY), nameof(PositionYLabel));
    }

    // ---------- Playback speed ----------

    /// <summary>
    /// Speed as a percentage; committed on release as a stretch (duration
    /// changes, source range untouched — same as Shift+edge drag).
    /// </summary>
    public double SpeedPercent
    {
        get => Math.Round(Math.Clamp(_event.PlaybackRate, 0.25, 4.0) * 100);
        set
        {
            _pendingSpeed = Math.Clamp(value / 100.0, 0.25, 4.0);
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeedLabel));
        }
    }

    private double? _pendingSpeed;

    public string SpeedLabel
    {
        get
        {
            var rate = _pendingSpeed ?? _event.PlaybackRate;
            var sourceSpan = Math.Max(0.01, _event.SourceOut - _event.SourceIn);
            return $"{rate * 100:0}%  →  {sourceSpan / rate:0.##}s";
        }
    }

    // ---------- Audio / video levels ----------

    public double VolumePercent
    {
        get => Math.Round(VolumeLimits.Clamp(_volumeTarget?.Volume ?? 1) * 100);
        set
        {
            if (_volumeTarget is null) return;
            _volumeTarget.Volume = VolumeLimits.Clamp(value / 100.0);
            Changed(nameof(VolumePercent), nameof(VolumeLabel));
        }
    }

    public string VolumeLabel => $"{VolumePercent:0}%";

    public double OpacityPercent
    {
        get => Math.Round(Math.Clamp(_event.Opacity, 0, 1) * 100);
        set { _event.Opacity = Math.Clamp(value / 100.0, 0, 1); Changed(nameof(OpacityPercent), nameof(OpacityLabel)); }
    }

    public string OpacityLabel => $"{OpacityPercent:0}%";

    public double FadeInSeconds
    {
        get => _event.FadeInDuration;
        set { _event.FadeInDuration = Math.Clamp(value, 0, 10); Changed(nameof(FadeInSeconds), nameof(FadeInLabel)); }
    }

    public string FadeInLabel => $"{FadeInSeconds:0.#}s";

    public double FadeOutSeconds
    {
        get => _event.FadeOutDuration;
        set { _event.FadeOutDuration = Math.Clamp(value, 0, 10); Changed(nameof(FadeOutSeconds), nameof(FadeOutLabel)); }
    }

    public string FadeOutLabel => $"{FadeOutSeconds:0.#}s";

    // ---------- Undo-friendly edit sessions (one command per slider drag) ----------

    public void BeginEdit(string key)
    {
        if (_editBaselines.ContainsKey(key)) return;
        _editBaselines[key] = key switch
        {
            "scale" => new[]
            {
                _event.Transform.ScaleX, _event.Transform.ScaleY,
                _event.Transform.PositionX, _event.Transform.PositionY
            },
            "posx" => new[] { _event.Transform.PositionX },
            "posy" => new[] { _event.Transform.PositionY },
            "speed" => new[] { _event.PlaybackRate },
            "volume" => new[] { _volumeTarget?.Volume ?? 1 },
            "opacity" => new[] { _event.Opacity },
            "fadein" => new[] { _event.FadeInDuration },
            "fadeout" => new[] { _event.FadeOutDuration },
            _ => Array.Empty<double>()
        };
    }

    public void EndEdit(string key)
    {
        if (!_editBaselines.Remove(key, out var old)) return;
        var evt = _event;

        switch (key)
        {
            case "scale":
                CommitTransform(old);
                break;

            case "posx":
                Commit("Move layer", old[0], evt.Transform.PositionX, v => evt.Transform.PositionX = v);
                break;

            case "posy":
                Commit("Move layer", old[0], evt.Transform.PositionY, v => evt.Transform.PositionY = v);
                break;

            case "speed":
                if (_pendingSpeed is { } rate && Math.Abs(rate - old[0]) > 0.001)
                {
                    var sourceSpan = Math.Max(0.01, evt.SourceOut - evt.SourceIn);
                    _run(new StretchEventCommand(evt, evt.Start, sourceSpan / rate));
                }
                _pendingSpeed = null;
                Changed(nameof(SpeedPercent), nameof(SpeedLabel));
                break;

            case "volume" when _volumeTarget is { } target:
                Commit($"Set volume of '{target.Name}'", old[0], target.Volume, v => target.Volume = v);
                break;

            case "opacity":
                Commit("Set opacity", old[0], evt.Opacity, v => evt.Opacity = v);
                break;

            case "fadein":
                Commit("Set fade in", old[0], evt.FadeInDuration, v => evt.FadeInDuration = v);
                break;

            case "fadeout":
                Commit("Set fade out", old[0], evt.FadeOutDuration, v => evt.FadeOutDuration = v);
                break;
        }
    }

    private void CommitTransform(double[] old)
    {
        var t = _event.Transform;
        var changed = Math.Abs(old[0] - t.ScaleX) > 0.001 || Math.Abs(old[1] - t.ScaleY) > 0.001 ||
                      Math.Abs(old[2] - t.PositionX) > 0.01 || Math.Abs(old[3] - t.PositionY) > 0.01;
        if (!changed) return;

        var commands = new List<IEditorCommand>
        {
            new SetValueCommand<double>("Scale X", old[0], t.ScaleX, v => t.ScaleX = v),
            new SetValueCommand<double>("Scale Y", old[1], t.ScaleY, v => t.ScaleY = v),
            new SetValueCommand<double>("Pos X", old[2], t.PositionX, v => t.PositionX = v),
            new SetValueCommand<double>("Pos Y", old[3], t.PositionY, v => t.PositionY = v)
        };
        RunCommitted(new CompositeCommand($"Resize '{_event.Name}'", commands));
    }

    private void Commit(string description, double oldValue, double newValue, Action<double> set)
    {
        if (Math.Abs(oldValue - newValue) < 0.0001) return;
        set(oldValue); // clean undo baseline; the command re-applies the new value
        _run(new SetValueCommand<double>(description, oldValue, newValue, set));
    }

    private void RunCommitted(CompositeCommand command)
    {
        // Rewind to the baseline first so undo lands exactly where the drag started.
        command.Undo();
        _run(command);
    }

    private void Changed(params string[] properties)
    {
        foreach (var property in properties) OnPropertyChanged(property);
        _previewRefresh();
    }
}
