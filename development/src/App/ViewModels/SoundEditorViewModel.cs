using System.Collections.ObjectModel;
using System.IO;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Services;
using VideoEditor.App.Ui;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;
using VideoEditor.Domain.Sound;
using VideoEditor.MediaEngine.Audio;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// State and operations of the sound editor: which clip is loaded, what is
/// selected, the piece list, the master effect chain, auditioning and the export
/// run. Non-destructive throughout — the source file is only ever read.
///
/// Edits are snapshot-undone rather than routed through
/// <c>IEditorCommand</c>/<c>UndoRedoService</c> on purpose: a sound-editor
/// session is scratch state, not part of the project model, so it must not push
/// entries onto the timeline's undo stack.
/// </summary>
public sealed class SoundEditorViewModel : ObservableObject, IDisposable
{
    /// <summary>How many edits back the window's Ctrl+Z can reach.</summary>
    private const int UndoDepth = 40;

    private static readonly HashSet<string> AudibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".ogg", ".opus", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif",
        ".mp4", ".mov", ".mkv", ".webm", ".avi"
    };

    private readonly SoundEditorContext _context;
    private readonly AudioClipExportService _exporter;
    private readonly SoundPreviewService _preview;
    private readonly List<SoundEditSession> _undoStack = new();

    private SoundEditSession? _session;
    private float[]? _peaks;
    private SoundSegmentViewModel? _selectedSegment;
    private double _selectionStart;
    private double _selectionEnd;
    private double _playheadTime;
    private double _zoom = 1;
    private bool _isPlaying;
    private bool _isExporting;
    private double _exportProgress;
    private string _exportStatus = string.Empty;
    private string _statusText = "Drop an audio file here, or drag one in from the media library.";
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _previewCts;

    public SoundEditorViewModel(SoundEditorContext context)
    {
        _context = context;
        _exporter = new AudioClipExportService(context.Ffmpeg, context.Catalog);
        _preview = new SoundPreviewService(context.Ffmpeg, context.Cache, context.Catalog);

        AvailableEffects = new ObservableCollection<EffectDefinitionViewModel>(
            context.Catalog.All
                .Where(definition => definition.Targets.HasFlag(EffectTarget.Audio))
                .Select(definition => new EffectDefinitionViewModel(definition)));
    }

    /// <summary>Raised whenever the waveform needs repainting.</summary>
    public event EventHandler? VisualsChanged;

    /// <summary>Raised when playback stops on its own (the audition ran out).</summary>
    public event EventHandler? PlaybackFinished;

    public SoundExportViewModel Export { get; } = new();

    public ObservableCollection<SoundSegmentViewModel> Segments { get; } = new();
    public ObservableCollection<EffectDefinitionViewModel> AvailableEffects { get; }
    public ObservableCollection<SoundEffectViewModel> AppliedEffects { get; } = new();

    public SoundEditSession? Session => _session;
    public float[]? Peaks => _peaks;
    public int PeaksPerSecond => TimelineVisualsService.WaveformPeaksPerSecond;

    public bool HasClip => _session is { Segments.Count: > 0 };

    /// <summary>Drives the "drop a file here" hint over the empty waveform.</summary>
    public bool HasNoClip => !HasClip;

    public bool FfmpegMissing => !_context.Ffmpeg.IsAvailable;

    /// <summary>Fade-curve names for the two easing combo boxes.</summary>
    public IReadOnlyList<string> EasingLabels => EasingOptions.Labels;

    public string ClipName => _session?.Name ?? "No sound loaded";
    public string SourcePathLabel => _session?.SourcePath ?? string.Empty;

    public string MetaLabel => _session is null
        ? "Drop a .wav, .mp3, .ogg or any video file to edit its sound."
        : $"source {TimeText.Compact(_session.SourceDuration)}  •  " +
          $"edit {TimeText.Compact(_session.OutputDuration)}  •  " +
          $"{_session.Segments.Count} piece{(_session.Segments.Count == 1 ? string.Empty : "s")}";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    // ---------- Selection and playhead, both in OUTPUT seconds ----------

    public double SelectionStart => Math.Min(_selectionStart, _selectionEnd);
    public double SelectionEnd => Math.Max(_selectionStart, _selectionEnd);
    public bool HasSelection => SelectionEnd - SelectionStart > SoundEditSession.MinSegmentDuration;

    public string SelectionLabel => HasSelection
        ? $"{TimeText.Compact(SelectionStart)} – {TimeText.Compact(SelectionEnd)}  " +
          $"({SelectionEnd - SelectionStart:0.##}s)"
        : "nothing selected — drag across the waveform";

    public double PlayheadTime
    {
        get => _playheadTime;
        set
        {
            var clamped = Math.Clamp(value, 0, _session?.OutputDuration ?? 0);
            if (!SetProperty(ref _playheadTime, clamped)) return;
            OnPropertyChanged(nameof(TimeLabel));
            RaiseVisuals();
        }
    }

    public string TimeLabel => _session is null
        ? "0:00.0 / 0:00.0"
        : $"{TimeText.Compact(_playheadTime)} / {TimeText.Compact(_session.OutputDuration)}";

    /// <summary>Horizontal magnification: 1 fits the whole clip in the viewport.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            var clamped = Math.Clamp(value, 1, 16);
            if (!SetProperty(ref _zoom, clamped)) return;
            OnPropertyChanged(nameof(ZoomLabel));
        }
    }

    public string ZoomLabel => $"{_zoom:0.#}×";

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public bool CanUndo => _undoStack.Count > 0;

    public SoundSegmentViewModel? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (!SetProperty(ref _selectedSegment, value)) return;
            OnPropertyChanged(nameof(HasSelectedSegment));
            RaiseVisuals();
        }
    }

    public bool HasSelectedSegment => _selectedSegment != null;

    // ---------- Loading ----------

    /// <summary>True when the file looks like something with a sound track in it.</summary>
    public static bool IsAudibleFile(string path) =>
        AudibleExtensions.Contains(Path.GetExtension(path));

    /// <summary>Loads a media library entry by id. False when it is not audible.</summary>
    public async Task<bool> LoadFromLibraryAsync(Guid mediaId)
    {
        if (_context.ResolveMedia(mediaId) is not { } media) return false;
        if (media.Type == MediaType.Image) return false;
        return await LoadFileAsync(media.FilePath, media.Name, media.DurationSeconds).ConfigureAwait(true);
    }

    /// <summary>
    /// Loads a file, probing it for a duration when one was not supplied.
    /// Replaces whatever was loaded before, undo history included.
    /// </summary>
    public async Task<bool> LoadFileAsync(string path, string? name = null, double? knownDuration = null)
    {
        if (!File.Exists(path) || !IsAudibleFile(path))
        {
            StatusText = "That file is not something with sound in it.";
            return false;
        }

        StopPlayback();
        var duration = knownDuration ?? 0;
        if (duration <= 0)
        {
            var info = await _context.Probe.ProbeAsync(path).ConfigureAwait(true);
            duration = info?.DurationSeconds ?? 0;
        }
        if (duration <= 0)
        {
            StatusText = _context.Ffmpeg.IsAvailable
                ? "That file's length could not be read, so it cannot be edited."
                : "FFmpeg is missing, so audio cannot be read. Install it with Tools → Download FFmpeg.";
            return false;
        }

        _session = SoundEditSession.ForFile(path, name ?? Path.GetFileName(path), duration);
        _undoStack.Clear();
        _peaks = null;
        _selectionStart = _selectionEnd = 0;
        _playheadTime = 0;
        StatusText = $"Loaded {_session.Name}.";
        RebuildFromSession();
        LoadPeaks(path);
        return true;
    }

    /// <summary>Waveform peaks arrive asynchronously; the strip repaints when they do.</summary>
    private void LoadPeaks(string path)
    {
        if (_context.Visuals.TryGetPeaks(path, out var cached))
        {
            _peaks = cached;
            RaiseVisuals();
            return;
        }

        _context.Visuals.RequestPeaks(path, peaks =>
        {
            if (_session?.SourcePath != path) return; // a different clip was loaded meanwhile
            _peaks = peaks;
            RaiseVisuals();
        });
    }

    // ---------- Editing ----------

    public void SetSelection(double from, double to)
    {
        _selectionStart = from;
        _selectionEnd = to;
        RaiseSelection();
    }

    public void SelectAll()
    {
        if (_session is null) return;
        SetSelection(0, _session.OutputDuration);
    }

    public void ClearSelection() => SetSelection(0, 0);

    /// <summary>Cuts the piece under the playhead in two.</summary>
    public void SplitAtPlayhead() =>
        Mutate(session => session.SplitAt(_playheadTime), "Split at the playhead.", "Nothing to split there.");

    /// <summary>Removes the selected span and closes the gap.</summary>
    public void DeleteSelection() =>
        Mutate(session => session.RemoveRange(SelectionStart, SelectionEnd),
            "Removed the selection.", "Select a span on the waveform first.");

    /// <summary>Throws away everything outside the selection.</summary>
    public void TrimToSelection() =>
        Mutate(session => session.TrimTo(SelectionStart, SelectionEnd),
            "Trimmed to the selection.", "Select a span on the waveform first.");

    /// <summary>Trims the clip's start or end to the playhead — the quick in/out cut.</summary>
    public void TrimEdgeToPlayhead(bool trimStart) =>
        Mutate(session => trimStart
                ? session.RemoveRange(0, _playheadTime)
                : session.RemoveRange(_playheadTime, session.OutputDuration),
            trimStart ? "Trimmed the start." : "Trimmed the end.",
            "The playhead is already at that edge.");

    public void RemoveSegment(Guid id) =>
        Mutate(session => session.Segments.Count > 1 && session.RemoveSegment(id),
            "Removed a piece.", "A clip has to keep at least one piece.");

    public void MoveSegment(Guid id, int delta) =>
        Mutate(session => session.MoveSegment(id, delta), "Reordered the pieces.", "Already at the edge.");

    /// <summary>Back to the untouched file, as one piece.</summary>
    public void ResetClip() => Mutate(session =>
    {
        session.Reset();
        session.MasterGain = VolumeLimits.Default;
        return true;
    }, "Back to the original sound.", string.Empty);

    public void Undo()
    {
        if (_undoStack.Count == 0 || _session is null) return;
        _session = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        StopPlayback();
        StatusText = "Undone.";
        OnPropertyChanged(nameof(CanUndo));
        RebuildFromSession();
    }

    /// <summary>Master level as a percentage, like the track and clip volumes.</summary>
    public double MasterGainPercent
    {
        get => Math.Round(VolumeLimits.Clamp(_session?.MasterGain ?? 1) * 100);
        set
        {
            if (_session is null) return;
            var clamped = VolumeLimits.Clamp(value / 100.0);
            if (Math.Abs(_session.MasterGain - clamped) < 0.0001) return;
            _session.MasterGain = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MasterGainLabel));
            RaiseVisuals();
        }
    }

    public string MasterGainLabel
    {
        get
        {
            var gain = VolumeLimits.Clamp(_session?.MasterGain ?? 1);
            return gain <= 0.0001 ? "silent" : $"{gain * 100:0}%  ({20 * Math.Log10(gain):+0.0;-0.0;0.0} dB)";
        }
    }

    // ---------- Master effect chain ----------

    public void AddEffect(string effectId)
    {
        if (_session is null) return;
        if (_context.Catalog.Find(effectId) is not { } definition) return;
        if (!definition.Targets.HasFlag(EffectTarget.Audio))
        {
            StatusText = $"{definition.Name} does not do anything to sound.";
            return;
        }

        PushUndo();
        _session.Effects.Add(definition.CreateInstance());
        StatusText = $"Added {definition.Name}.";
        RebuildEffects();
        RaiseVisuals();
    }

    public void RemoveEffect(Guid instanceId)
    {
        if (_session is null) return;
        var instance = _session.Effects.FirstOrDefault(e => e.Id == instanceId);
        if (instance is null) return;

        PushUndo();
        _session.Effects.Remove(instance);
        StatusText = "Removed an effect.";
        RebuildEffects();
        RaiseVisuals();
    }

    public void MoveEffect(Guid instanceId, int delta)
    {
        if (_session is null || delta == 0) return;
        var instance = _session.Effects.FirstOrDefault(e => e.Id == instanceId);
        if (instance is null) return;

        var from = _session.Effects.IndexOf(instance);
        var to = Math.Clamp(from + delta, 0, _session.Effects.Count - 1);
        if (to == from) return;

        PushUndo();
        _session.Effects.RemoveAt(from);
        _session.Effects.Insert(to, instance);
        RebuildEffects();
    }

    // ---------- Auditioning ----------

    /// <summary>Renders from the playhead and plays it; returns the length queued.</summary>
    public async Task<double> PlayAsync()
    {
        if (_session is null || _session.IsEmpty) return 0;

        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        StatusText = "Rendering the audition…";
        IsPlaying = true;
        try
        {
            var length = await _preview
                .StartAsync(_session, Export.BuildSettings(string.Empty), _playheadTime, cts.Token)
                .ConfigureAwait(true);
            if (length <= 0)
            {
                IsPlaying = false;
                StatusText = FfmpegMissing
                    ? "FFmpeg is missing, so nothing can be played."
                    : "There is nothing to play from here.";
                return 0;
            }

            var capped = length < _session.OutputDuration - _playheadTime
                ? $" Only {length:0}s is auditioned at a time."
                : string.Empty;
            var levelNote = Export.NormalizeIndex > 0
                ? " Normalization happens when the file is written, so the export lands at a different level."
                : string.Empty;
            StatusText = "Playing." + capped + levelNote;
            return length;
        }
        catch (OperationCanceledException)
        {
            IsPlaying = false;
            return 0;
        }
    }

    public void StopPlayback()
    {
        _previewCts?.Cancel();
        _previewCts = null;
        _preview.Stop();
        if (IsPlaying) StatusText = "Stopped.";
        IsPlaying = false;
    }

    /// <summary>Called by the window's clock when the audition has run out.</summary>
    public void NotifyPlaybackFinished()
    {
        _preview.Stop();
        IsPlaying = false;
        PlaybackFinished?.Invoke(this, EventArgs.Empty);
    }

    // ---------- Export ----------

    public bool IsExporting
    {
        get => _isExporting;
        private set => SetProperty(ref _isExporting, value);
    }

    public double ExportProgress
    {
        get => _exportProgress;
        private set => SetProperty(ref _exportProgress, value);
    }

    public string ExportStatus
    {
        get => _exportStatus;
        private set => SetProperty(ref _exportStatus, value);
    }

    /// <summary>Folder and file name to offer in the save dialog.</summary>
    public (string Folder, string FileName, string Filter) ExportTarget() =>
        (_context.DefaultExportFolder,
            Export.SuggestFileName(_session?.Name ?? "sound"),
            Export.Format.SaveDialogFilter());

    /// <summary>Renders the edited sound to <paramref name="outputPath"/>.</summary>
    public async Task<AudioExportResult> ExportAsync(string outputPath)
    {
        if (_session is null || _session.IsEmpty)
            return AudioExportResult.Failed("There is nothing to export yet.");

        StopPlayback();
        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportProgress = 0;
        ExportStatus = "Starting…";

        try
        {
            var settings = Export.BuildSettings(outputPath);
            var result = await _exporter.ExportAsync(
                _session, settings,
                new Progress<double>(value => ExportProgress = value),
                new Progress<string>(text => ExportStatus = text),
                _exportCts.Token).ConfigureAwait(true);

            StatusText = result.Success
                ? $"Exported {Path.GetFileName(outputPath)}."
                : "Export failed.";
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export cancelled.";
            return AudioExportResult.Stopped();
        }
        finally
        {
            IsExporting = false;
            ExportStatus = string.Empty;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    public void CancelExport() => _exportCts?.Cancel();

    public void Dispose()
    {
        _exportCts?.Cancel();
        _previewCts?.Cancel();
        _preview.Dispose();
    }

    // ---------- Internals ----------

    /// <summary>
    /// Runs one edit under undo: snapshots, applies, and reports through the
    /// status line. A refused edit leaves no undo entry behind.
    /// </summary>
    private void Mutate(Func<SoundEditSession, bool> edit, string success, string refused)
    {
        if (_session is null) return;

        var snapshot = _session.Copy();
        bool changed;
        try
        {
            changed = edit(_session);
        }
        catch (Exception ex)
        {
            _session = snapshot; // an edit must never leave a half-applied clip
            StatusText = ex.Message;
            RebuildFromSession();
            return;
        }

        if (!changed)
        {
            if (refused.Length > 0) StatusText = refused;
            return;
        }

        Remember(snapshot);
        StopPlayback();
        StatusText = success;
        RebuildFromSession();
    }

    /// <summary>
    /// Snapshots before a slider drag, so one drag is one undo step (the app's
    /// slider pattern: live value while dragging, one history entry).
    /// </summary>
    public void BeginSliderEdit() => PushUndo();

    /// <summary>Snapshots the current state for a mutation made outside <see cref="Mutate"/>.</summary>
    private void PushUndo()
    {
        if (_session is null) return;
        Remember(_session.Copy());
    }

    private void Remember(SoundEditSession snapshot)
    {
        _undoStack.Add(snapshot);
        if (_undoStack.Count > UndoDepth) _undoStack.RemoveAt(0);
        OnPropertyChanged(nameof(CanUndo));
    }

    /// <summary>Rebuilds every list and label from the session model.</summary>
    private void RebuildFromSession()
    {
        var previousSegmentId = _selectedSegment?.Id;
        Segments.Clear();

        if (_session is { } session)
        {
            var start = 0.0;
            for (var i = 0; i < session.Segments.Count; i++)
            {
                var segment = session.Segments[i];
                Segments.Add(new SoundSegmentViewModel(segment, i, start, OnLiveValueChanged));
                start += segment.Duration;
            }

            var total = session.OutputDuration;
            _selectionStart = Math.Clamp(_selectionStart, 0, total);
            _selectionEnd = Math.Clamp(_selectionEnd, 0, total);
            _playheadTime = Math.Clamp(_playheadTime, 0, total);
            Export.SetClipDuration(total);
        }

        SelectedSegment = Segments.FirstOrDefault(s => s.Id == previousSegmentId) ?? Segments.FirstOrDefault();
        RebuildEffects();

        OnPropertyChanged(nameof(HasClip));
        OnPropertyChanged(nameof(HasNoClip));
        OnPropertyChanged(nameof(ClipName));
        OnPropertyChanged(nameof(SourcePathLabel));
        OnPropertyChanged(nameof(MetaLabel));
        OnPropertyChanged(nameof(MasterGainPercent));
        OnPropertyChanged(nameof(MasterGainLabel));
        OnPropertyChanged(nameof(PlayheadTime));
        OnPropertyChanged(nameof(TimeLabel));
        RaiseSelection();
    }

    private void RebuildEffects()
    {
        AppliedEffects.Clear();
        if (_session is null) return;

        foreach (var instance in _session.Effects)
        {
            AppliedEffects.Add(new SoundEffectViewModel(
                instance, _context.Catalog.Find(instance.Type), OnLiveValueChanged));
        }
        OnPropertyChanged(nameof(HasEffects));
        OnPropertyChanged(nameof(HasNoEffects));
    }

    public bool HasEffects => AppliedEffects.Count > 0;

    /// <summary>Drives the "no effects yet" hint under the chain.</summary>
    public bool HasNoEffects => AppliedEffects.Count == 0;

    /// <summary>
    /// A live slider moved: the model already holds the new value, so the
    /// waveform and the labels just need to catch up. No undo entry — the
    /// snapshot for a slider drag is taken when the drag starts.
    /// </summary>
    private void OnLiveValueChanged()
    {
        OnPropertyChanged(nameof(MetaLabel));
        RaiseVisuals();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectionStart));
        OnPropertyChanged(nameof(SelectionEnd));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionLabel));
        RaiseVisuals();
    }

    private void RaiseVisuals() => VisualsChanged?.Invoke(this, EventArgs.Empty);
}
