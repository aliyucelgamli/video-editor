using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Services;
using VideoEditor.Application.Commands;
using VideoEditor.Application.Effects;
using VideoEditor.Application.Services;
using VideoEditor.Application.UndoRedo;
using VideoEditor.Domain;
using VideoEditor.MediaEngine;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Export;
using VideoEditor.MediaEngine.Ffmpeg;
using VideoEditor.MediaEngine.Frames;
using VideoEditor.MediaEngine.Thumbnails;
using VideoEditor.MediaEngine.Waveform;
using VideoEditor.ProjectIO;

namespace VideoEditor.App.ViewModels;

public class MainViewModel : ObservableObject
{
    public const double MinPixelsPerSecond = 2;
    public const double MaxPixelsPerSecond = 240;

    /// <summary>Fallback event length until FFmpeg probing provides real durations.</summary>
    public const double DefaultEventDuration = 5.0;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm",
        ".wav", ".mp3", ".aac", ".flac", ".ogg",
        ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".tiff"
    };

    private readonly UndoRedoService _undoRedo = new();
    private readonly ProjectService _projects;
    private readonly EffectCatalog _catalog;
    private readonly UserEffectLibrary _userEffects;
    private readonly FFmpegLocator _ffmpeg;
    private readonly MediaEnrichmentService _enrichment;
    private readonly TimelineVisualsService _visuals;
    private readonly ExportService _exporter;
    private readonly DispatcherTimer _previewRefreshTimer;

    private string _statusText = "Ready — drop media files into the library or a track";
    private double _pixelsPerSecond = 20.0;
    private Guid? _selectedEventId;
    private TimeRange? _rangeDragPreview;
    private bool _isExporting;
    private double _exportProgress;
    private CancellationTokenSource? _exportCts;

    /// <summary>Raised when the view should zoom the timeline by a factor (anchored in view code).</summary>
    public event EventHandler<double>? ZoomRequested;

    public MainViewModel()
    {
        _projects = new ProjectService(new JsonProjectSerializer(), _undoRedo);
        _projects.ProjectChanged += (_, _) => { _selectedEventId = null; RebuildFromModel(); };
        _projects.StateChanged += (_, _) => OnPropertyChanged(nameof(WindowTitle));
        _undoRedo.StateChanged += (_, _) => { RebuildFromModel(); RequestPreviewRefresh(); };

        // ---- Media engine wiring (all features degrade gracefully without ffmpeg) ----
        var appRoot = CachePaths.LocateAppRoot();
        var cache = CachePaths.Locate();
        _ffmpeg = new FFmpegLocator(appRoot);
        var dispatcher = Dispatcher.CurrentDispatcher;
        _enrichment = new MediaEnrichmentService(new MediaProbe(_ffmpeg), dispatcher);
        _visuals = new TimelineVisualsService(
            new ThumbnailService(_ffmpeg, cache), new WaveformService(_ffmpeg, cache), dispatcher);

        _catalog = new EffectCatalog();
        _userEffects = new UserEffectLibrary(
            _catalog, new VefxSerializer(), Path.Combine(appRoot, "user", "effects"));
        _userEffects.LoadAll();

        var extractor = new FrameExtractor(_ffmpeg);
        var effectPipeline = new VideoEffectPipeline(_catalog);
        var compositor = new FrameCompositor(extractor, effectPipeline);
        _exporter = new ExportService(_ffmpeg, compositor, _catalog);

        var previewAudio = new PreviewAudioService(_ffmpeg, cache, _catalog);
        Preview = new PreviewViewModel(
            compositor, extractor, effectPipeline, _ffmpeg, () => _projects.Current, previewAudio);
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewViewModel.PlayheadTime))
                OnPropertyChanged(nameof(PlayheadX));
        };

        Effects = new EffectsPanelViewModel(
            _catalog, _userEffects,
            execute: c => _undoRedo.ExecuteCommand(c),
            getSelected: GetSelectedContext,
            setStatus: s => StatusText = s,
            previewRefresh: RequestPreviewRefresh);

        // Short debounce: with the extractor's frame cache, re-rendering the same
        // playhead position only re-runs effects, so sliders respond almost live.
        _previewRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _previewRefreshTimer.Tick += (_, _) => { _previewRefreshTimer.Stop(); Preview.RequestRender(); };

        NewProjectCommand = new RelayCommand(NewProject);
        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(Save);
        SaveAsCommand = new RelayCommand(SaveAs);
        ImportMediaCommand = new RelayCommand(ImportMediaDialog);
        UndoCommand = new RelayCommand(() => _undoRedo.Undo(), () => _undoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => _undoRedo.Redo(), () => _undoRedo.CanRedo);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => _selectedEventId is not null);
        ZoomInCommand = new RelayCommand(() => ZoomRequested?.Invoke(this, 1.25));
        ZoomOutCommand = new RelayCommand(() => ZoomRequested?.Invoke(this, 0.8));
        PlayPauseCommand = new RelayCommand(() => Preview.TogglePlay());
        SetRangeStartCommand = new RelayCommand(() => SetRangeEdge(isStart: true));
        SetRangeEndCommand = new RelayCommand(() => SetRangeEdge(isStart: false));
        ClearRangeCommand = new RelayCommand(ClearRange, () => HasExplicitRange);
        ExportCommand = new RelayCommand(Export, () => !_isExporting);
        CancelExportCommand = new RelayCommand(() => _exportCts?.Cancel(), () => _isExporting);
        DownloadFfmpegCommand = new RelayCommand(DownloadFfmpeg, () => !_isInstallingFfmpeg);
        UnlinkSelectedCommand = new RelayCommand(
            () => { if (_selectedEventId is Guid id) UnlinkEvent(id); },
            () => GetSelectedContext()?.Event.LinkedEventId != null);

        RebuildFromModel();

        if (FfmpegMissing)
            StatusText = "FFmpeg not found — preview, thumbnails, waveforms and export are disabled. " +
                         "Install FFmpeg and add it to PATH (or put it in tools\\ffmpeg\\).";
    }

    /// <summary>True when ffmpeg/ffprobe could not be located — media features are off.</summary>
    public bool FfmpegMissing => !_ffmpeg.IsAvailable && !_isInstallingFfmpeg;

    // ---------- One-click FFmpeg install ----------

    private bool _isInstallingFfmpeg;

    public bool IsInstallingFfmpeg
    {
        get => _isInstallingFfmpeg;
        private set
        {
            if (!SetProperty(ref _isInstallingFfmpeg, value)) return;
            OnPropertyChanged(nameof(FfmpegMissing));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async void DownloadFfmpeg()
    {
        IsInstallingFfmpeg = true;
        var progress = new Progress<double>(p =>
            StatusText = $"Downloading FFmpeg… {p:P0} (about 90 MB, one time only)");

        try
        {
            await new FfmpegInstaller().InstallAsync(_ffmpeg.ToolsDirectory, progress);
            _ffmpeg.Refresh();

            if (_ffmpeg.IsAvailable)
            {
                StatusText = "FFmpeg installed — preview, waveforms and export are ready";
                _enrichment.Enrich(_projects.Current.Media.Items.ToList(), _projects.Current,
                    DefaultEventDuration, () => { RebuildFromModel(); RequestPreviewRefresh(); });
                RebuildFromModel();
                RequestPreviewRefresh();
            }
            else
            {
                StatusText = "FFmpeg download finished but the executable was not found — see logs";
            }
        }
        catch (Exception ex)
        {
            StatusText = "FFmpeg download failed";
            MessageBox.Show(
                "FFmpeg could not be downloaded automatically.\n\n" + ex.Message +
                $"\n\nManual install: download from {FFmpegLocator.DownloadUrl} and copy " +
                "ffmpeg.exe + ffprobe.exe into tools\\ffmpeg\\ next to run.bat.",
                "Download Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsInstallingFfmpeg = false;
            OnPropertyChanged(nameof(FfmpegMissing));
        }
    }

    public PreviewViewModel Preview { get; }
    public EffectsPanelViewModel Effects { get; }

    public ObservableCollection<TrackViewModel> Tracks { get; } = new();
    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = new();
    public ObservableCollection<RulerTickViewModel> RulerTicks { get; } = new();

    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand ImportMediaCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand SetRangeStartCommand { get; }
    public RelayCommand SetRangeEndCommand { get; }
    public RelayCommand ClearRangeCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand CancelExportCommand { get; }
    public RelayCommand DownloadFfmpegCommand { get; }
    public RelayCommand UnlinkSelectedCommand { get; }

    public double PixelsPerSecond => _pixelsPerSecond;

    public double TimelineWidth { get; private set; } = 1200;

    public string WindowTitle
    {
        get
        {
            var dirty = _projects.IsDirty ? " *" : string.Empty;
            return $"{_projects.Current.Settings.Name}{dirty} — Video Editor";
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string TimelineInfoText =>
        $"Duration {FormatTime(_projects.Current.Duration)}   •   Zoom {_pixelsPerSecond:0.#} px/s";

    public string HistoryText => $"Undo {_undoRedo.UndoCount}  •  Redo {_undoRedo.RedoCount}";

    public string UndoToolTip => _undoRedo.UndoDescription is { } d ? $"Undo: {d}  (Z)" : "Undo (Z)";
    public string RedoToolTip => _undoRedo.RedoDescription is { } d ? $"Redo: {d}  (Y)" : "Redo (Y)";

    private string ProjectFileFilter =>
        $"Video Editor Project (*{_projects.DefaultExtension})|*{_projects.DefaultExtension}|All Files (*.*)|*.*";

    // ---------- Playhead ----------

    public double PlayheadX => Preview.PlayheadTime * _pixelsPerSecond;

    /// <summary>Moves the playhead (timeline click / ruler scrub) and renders that frame.</summary>
    public void SeekTo(double time) => Preview.Seek(Math.Max(0, time));

    /// <summary>Debounced preview re-render (slider drags, effect edits…).</summary>
    public void RequestPreviewRefresh()
    {
        _previewRefreshTimer.Stop();
        _previewRefreshTimer.Start();
    }

    // ---------- Export range (yellow bars) ----------

    /// <summary>
    /// The bars are always visible: with no explicit range they sit at the
    /// project's start/end (and export covers everything). Dragging a bar or
    /// pressing I/O creates an explicit range.
    /// </summary>
    private TimeRange? CurrentRange =>
        _rangeDragPreview
        ?? _projects.Current.ExportRange
        ?? (_projects.Current.Duration > 0.01
            ? new TimeRange { Start = 0, End = _projects.Current.Duration }
            : null);

    public bool HasRange => CurrentRange != null;

    /// <summary>True only when the user actually set a range (not the fallback).</summary>
    public bool HasExplicitRange => _projects.Current.ExportRange != null;
    public double RangeStartX => (CurrentRange?.Start ?? 0) * _pixelsPerSecond;
    public double RangeEndX => (CurrentRange?.End ?? 0) * _pixelsPerSecond;
    public double RangeWidth => Math.Max(0, RangeEndX - RangeStartX);

    public string RangeLabel => CurrentRange is { } range
        ? $"Range {FormatTime(range.Start)} – {FormatTime(range.End)}"
        : string.Empty;

    private void NotifyRangeChanged()
    {
        OnPropertyChanged(nameof(HasRange));
        OnPropertyChanged(nameof(HasExplicitRange));
        OnPropertyChanged(nameof(RangeStartX));
        OnPropertyChanged(nameof(RangeEndX));
        OnPropertyChanged(nameof(RangeWidth));
        OnPropertyChanged(nameof(RangeLabel));
    }

    /// <summary>I / O keys: set the yellow start/end bar at the playhead.</summary>
    private void SetRangeEdge(bool isStart)
    {
        var playhead = Preview.PlayheadTime;
        var duration = Math.Max(_projects.Current.Duration, playhead + 1);
        var current = _projects.Current.ExportRange;

        var range = current?.Clone() ?? new TimeRange { Start = 0, End = duration };
        if (isStart) range.Start = playhead;
        else range.End = playhead;
        CommitRange(range.Normalized());
        StatusText = isStart
            ? $"Export start set to {FormatTime(playhead)} (I)"
            : $"Export end set to {FormatTime(playhead)} (O)";
    }

    private void ClearRange()
    {
        if (_projects.Current.ExportRange is null) return;
        CommitRange(null);
        StatusText = "Export range cleared — exports now cover the whole project";
    }

    /// <summary>Live visual update while a yellow bar is being dragged (no undo entries).</summary>
    public void PreviewRangeDrag(double start, double end)
    {
        _rangeDragPreview = new TimeRange { Start = start, End = end }.Normalized();
        NotifyRangeChanged();
    }

    /// <summary>Finishes a yellow-bar drag with a single undoable command.</summary>
    public void CommitRangeDrag()
    {
        if (_rangeDragPreview is not { } preview) return;
        _rangeDragPreview = null;
        CommitRange(preview);
    }

    private void CommitRange(TimeRange? newRange)
    {
        var old = _projects.Current.ExportRange;
        var project = _projects.Current;
        _undoRedo.ExecuteCommand(new SetValueCommand<TimeRange?>(
            newRange is null ? "Clear export range" : "Set export range",
            old, newRange, r => project.ExportRange = r));
    }

    // ---------- Export ----------

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public double ExportProgress
    {
        get => _exportProgress;
        private set => SetProperty(ref _exportProgress, value);
    }

    private async void Export()
    {
        if (!_ffmpeg.IsAvailable)
        {
            MessageBox.Show(
                "FFmpeg was not found, so the project cannot be rendered.\n\n" +
                "Install FFmpeg (and ffprobe) and either add it to PATH, put it in " +
                "tools\\ffmpeg\\ next to the app, or set the VIDEOEDITOR_FFMPEG_DIR " +
                $"environment variable.\n\nDownload: {FFmpegLocator.DownloadUrl}",
                "FFmpeg Missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_projects.Current.Duration <= 0.01)
        {
            StatusText = "Nothing to export — the timeline is empty";
            return;
        }

        var exportDir = Path.Combine(CachePaths.LocateAppRoot(), "user", "exports");
        try { Directory.CreateDirectory(exportDir); } catch { exportDir = string.Empty; }

        var dialog = new SaveFileDialog
        {
            Filter = "MP4 Video (*.mp4)|*.mp4",
            FileName = _projects.Current.Settings.Name + ".mp4",
            InitialDirectory = exportDir
        };
        if (dialog.ShowDialog() != true) return;

        var settings = ExportSettings.FromProject(_projects.Current, dialog.FileName);
        var rangeText = settings.Range != null ? " (selected range)" : " (whole project)";

        IsExporting = true;
        ExportProgress = 0;
        _exportCts = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            ExportProgress = p;
            StatusText = $"Exporting{rangeText}… {p:P0}";
        });

        try
        {
            await _exporter.ExportAsync(_projects.Current, settings, progress, _exportCts.Token);
            StatusText = $"Exported to {Path.GetFileName(dialog.FileName)}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export cancelled";
            TryDelete(dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusText = "Export failed";
            MessageBox.Show($"The video could not be exported.\n\n{ex.Message}",
                "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsExporting = false;
            ExportProgress = 0;
            _exportCts = null;
        }
    }

    // ---------- Project lifecycle ----------

    public bool ConfirmDiscardChanges()
    {
        if (!_projects.IsDirty) return true;
        var result = MessageBox.Show(
            "The project has unsaved changes. Continue and discard them?",
            "Unsaved Changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private void NewProject()
    {
        if (!ConfirmDiscardChanges()) return;
        _projects.NewProject();
        StatusText = "New project created";
    }

    private void Open()
    {
        if (!ConfirmDiscardChanges()) return;
        var dialog = new OpenFileDialog { Filter = ProjectFileFilter };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _projects.Open(dialog.FileName);
            StatusText = $"Opened {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The project could not be opened.\n\n{ex.Message}",
                "Open Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Save()
    {
        if (_projects.CurrentFilePath is null)
        {
            SaveAs();
            return;
        }
        TrySave(() => _projects.Save());
    }

    private void SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = ProjectFileFilter,
            FileName = _projects.Current.Settings.Name + _projects.DefaultExtension
        };
        if (dialog.ShowDialog() != true) return;
        TrySave(() => _projects.SaveAs(dialog.FileName));
    }

    private void TrySave(Action save)
    {
        try
        {
            save();
            StatusText = $"Saved to {Path.GetFileName(_projects.CurrentFilePath)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The project could not be saved.\n\n{ex.Message}",
                "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Import ----------

    private void ImportMediaDialog()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media Files|*.mp4;*.mov;*.avi;*.mkv;*.webm;" +
                     "*.wav;*.mp3;*.aac;*.flac;*.ogg;" +
                     "*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tiff|" +
                     "Effects (*.vefx)|*.vefx|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        ImportFiles(dialog.FileNames);
    }

    /// <summary>Imports files into the media library; .vefx files go to the effect catalog.</summary>
    public void ImportFiles(IEnumerable<string> paths)
    {
        var allPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var vefxFiles = allPaths.Where(IsVefx).ToList();
        if (vefxFiles.Count > 0) Effects.ImportVefxFiles(vefxFiles);

        var items = BuildImportItems(allPaths.Where(p => !IsVefx(p)), out var skipped);
        if (items.Count == 0)
        {
            if (vefxFiles.Count > 0) return; // status already set by the effect import
            StatusText = skipped > 0
                ? $"Nothing imported — {skipped} unsupported or duplicate file(s)"
                : "Nothing to import";
            return;
        }

        var commands = items
            .Select(item => (IEditorCommand)new AddMediaCommand(_projects.Current, item))
            .ToList();
        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand($"Import {commands.Count} files", commands));
        EnrichNewMedia(items);

        StatusText = skipped > 0
            ? $"Imported {items.Count} file(s), skipped {skipped}"
            : $"Imported {items.Count} file(s)";
    }

    private static bool IsVefx(string path) =>
        string.Equals(Path.GetExtension(path), ".vefx", StringComparison.OrdinalIgnoreCase);

    private void EnrichNewMedia(IEnumerable<MediaItem> items) =>
        _enrichment.Enrich(items, _projects.Current, DefaultEventDuration, () =>
        {
            RebuildFromModel();
            RequestPreviewRefresh();
        });

    private List<MediaItem> BuildImportItems(IEnumerable<string> paths, out int skipped)
    {
        var result = new List<MediaItem>();
        skipped = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var supported = SupportedExtensions.Contains(Path.GetExtension(path));
            var duplicate = _projects.Current.Media.Items
                .Any(m => string.Equals(m.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (!supported || duplicate)
            {
                skipped++;
                continue;
            }
            result.Add(CreateMediaItem(path));
        }
        return result;
    }

    private static MediaItem CreateMediaItem(string path) => new()
    {
        Name = Path.GetFileName(path),
        FilePath = path,
        Type = DetectMediaType(path),
        FileSizeBytes = TryGetFileSize(path)
    };

    // ---------- Timeline drag & drop ----------

    public static bool IsCompatible(MediaType media, TrackType track) => track switch
    {
        TrackType.Audio => media == MediaType.Audio,
        _ => media is MediaType.Video or MediaType.Image
    };

    public bool CanDropMediaOnTrack(Guid mediaId, Guid trackId)
    {
        var media = _projects.Current.Media.FindById(mediaId);
        var track = _projects.Current.FindTrack(trackId);
        return media != null && track != null && IsCompatible(media.Type, track.Type);
    }

    public void DropMediaOnTrack(Guid mediaId, Guid trackId, double time)
    {
        var media = _projects.Current.Media.FindById(mediaId);
        var track = _projects.Current.FindTrack(trackId);
        if (media is null || track is null) return;

        if (!IsCompatible(media.Type, track.Type))
        {
            StatusText = $"A {media.Type} clip cannot go on a {track.Type} track";
            return;
        }

        var commands = new List<IEditorCommand>();
        var evt = BuildPlacement(media, track, Math.Max(0, time), commands);
        _selectedEventId = evt.Id;
        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand($"Add '{media.Name}' to timeline", commands));
        StatusText = $"Placed '{media.Name}' at {FormatTime(evt.Start)}";
    }

    /// <summary>Drops Explorer files straight onto a track: imports (if new) and places compatible clips.</summary>
    public void DropFilesOnTrack(Guid trackId, IEnumerable<string> paths, double time)
    {
        var track = _projects.Current.FindTrack(trackId);
        if (track is null) return;

        var commands = new List<IEditorCommand>();
        var newItems = new List<MediaItem>();
        var cursor = Math.Max(0, time);
        int placed = 0, imported = 0, skipped = 0;
        Guid? lastEventId = null;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IsVefx(path))
            {
                Effects.ImportVefxFiles(new[] { path });
                continue;
            }
            if (!SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                skipped++;
                continue;
            }

            var item = _projects.Current.Media.Items
                .FirstOrDefault(m => string.Equals(m.FilePath, path, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = CreateMediaItem(path);
                commands.Add(new AddMediaCommand(_projects.Current, item));
                newItems.Add(item);
                imported++;
            }

            if (IsCompatible(item.Type, track.Type))
            {
                var evt = BuildPlacement(item, track, cursor, commands);
                cursor = evt.End;
                placed++;
                lastEventId = evt.Id;
            }
        }

        if (commands.Count == 0)
        {
            if (skipped > 0) StatusText = $"Skipped {skipped} unsupported file(s)";
            return;
        }

        if (lastEventId is Guid id) _selectedEventId = id;
        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand("Add media to timeline", commands));
        EnrichNewMedia(newItems);

        var parts = new List<string>();
        if (placed > 0) parts.Add($"placed {placed} clip(s)");
        if (imported > 0) parts.Add($"imported {imported} file(s)");
        if (skipped > 0) parts.Add($"skipped {skipped}");
        StatusText = char.ToUpper(parts[0][0]) + string.Join(", ", parts)[1..];
    }

    /// <summary>
    /// Removes media references from the library (Delete key, multi-select).
    /// Items still used by a timeline event are kept — the project must never
    /// lose clips silently. Files on disk are never touched.
    /// </summary>
    public void RemoveMediaItems(IReadOnlyList<Guid> mediaIds)
    {
        if (mediaIds.Count == 0) return;

        var usedIds = _projects.Current.Tracks
            .SelectMany(t => t.Events)
            .Select(e => e.MediaId)
            .ToHashSet();

        var commands = new List<IEditorCommand>();
        var skippedInUse = 0;
        foreach (var id in mediaIds)
        {
            if (_projects.Current.Media.FindById(id) is not { } item) continue;
            if (usedIds.Contains(id)) { skippedInUse++; continue; }
            commands.Add(new RemoveMediaCommand(_projects.Current, item));
        }

        if (commands.Count > 0)
        {
            _undoRedo.ExecuteCommand(commands.Count == 1
                ? commands[0]
                : new CompositeCommand($"Remove {commands.Count} media from library", commands));
        }

        StatusText = (commands.Count, skippedInUse) switch
        {
            (0, > 0) => "Nothing removed — the selected media is used on the timeline",
            (_, 0) => $"Removed {commands.Count} item(s) from the library (files stay on disk)",
            _ => $"Removed {commands.Count} item(s); kept {skippedInUse} still used on the timeline"
        };
    }

    /// <summary>Double-click in the library: appends the clip to the end of the first compatible track.</summary>
    public void AddMediaToTimelineEnd(Guid mediaId)
    {
        var media = _projects.Current.Media.FindById(mediaId);
        if (media is null) return;

        var track = _projects.Current.Tracks.FirstOrDefault(t => IsCompatible(media.Type, t.Type));
        if (track is null)
        {
            StatusText = $"No compatible track for a {media.Type} clip — add one first";
            return;
        }

        var time = track.Events.Count == 0 ? 0 : track.Events.Max(e => e.End);
        DropMediaOnTrack(media.Id, track.Id, time);
    }

    /// <summary>
    /// Plans the placement of a clip: the event itself plus — for video files
    /// with sound — a linked audio event on an audio track (VEGAS-style pairs).
    /// </summary>
    private TimelineEvent BuildPlacement(
        MediaItem media, Track track, double start, List<IEditorCommand> commands)
    {
        var evt = CreateEvent(media, start);
        commands.Add(new AddEventCommand(track, evt));

        var wantsLinkedAudio = media.Type == MediaType.Video &&
                               track.Type != TrackType.Audio &&
                               media.HasAudio != false; // unknown (not probed yet) counts as yes
        if (wantsLinkedAudio)
        {
            var audioTrack = _projects.Current.Tracks.FirstOrDefault(t => t.Type == TrackType.Audio);
            if (audioTrack is null)
            {
                audioTrack = new Track { Name = "A1", Type = TrackType.Audio };
                commands.Add(new AddTrackCommand(_projects.Current, audioTrack));
            }

            var audioEvent = CreateEvent(media, start);
            audioEvent.LinkedEventId = evt.Id;
            evt.LinkedEventId = audioEvent.Id;
            commands.Add(new AddEventCommand(audioTrack, audioEvent));
        }

        return evt;
    }

    private static TimelineEvent CreateEvent(MediaItem media, double start)
    {
        var duration = media.DurationSeconds ?? DefaultEventDuration;
        return new TimelineEvent
        {
            MediaId = media.Id,
            Name = media.Name,
            Start = Math.Max(0, Math.Round(start, 2)),
            Duration = duration,
            SourceIn = 0,
            SourceOut = duration
        };
    }

    // ---------- Moving events ----------

    /// <summary>
    /// Snaps a desired start time to nearby event edges, the playhead and the
    /// yellow range bars (within ~8 px at the current zoom).
    /// </summary>
    public double SnapTime(double desiredStart, double eventDuration, Guid movingEventId)
    {
        var threshold = 8.0 / _pixelsPerSecond;
        var moving = _projects.Current.FindEvent(movingEventId)?.Event;
        var linkedId = moving?.LinkedEventId;

        var snapPoints = new List<double> { 0, Preview.PlayheadTime };
        if (_projects.Current.ExportRange is { } range)
        {
            snapPoints.Add(range.Start);
            snapPoints.Add(range.End);
        }
        foreach (var track in _projects.Current.Tracks)
            foreach (var evt in track.Events)
            {
                if (evt.Id == movingEventId || evt.Id == linkedId) continue;
                snapPoints.Add(evt.Start);
                snapPoints.Add(evt.End);
            }

        var best = desiredStart;
        var bestDistance = threshold;
        foreach (var point in snapPoints)
        {
            // Snap the clip's start edge…
            var distance = Math.Abs(point - desiredStart);
            if (distance < bestDistance) { bestDistance = distance; best = point; }
            // …or its end edge.
            distance = Math.Abs(point - (desiredStart + eventDuration));
            if (distance < bestDistance) { bestDistance = distance; best = point - eventDuration; }
        }
        return Math.Max(0, best);
    }

    /// <summary>Moves an event (and its linked partner) to a new start — one undo step.</summary>
    public void MoveEvent(Guid eventId, double newStart)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;
        var (track, evt) = found;
        newStart = Math.Max(0, newStart);
        if (Math.Abs(newStart - evt.Start) < 0.0005) return;

        var commands = new List<IEditorCommand> { new MoveEventCommand(evt, track, track, newStart) };

        if (evt.LinkedEventId is Guid linkedId &&
            _projects.Current.FindEvent(linkedId) is { } linked)
        {
            var delta = newStart - evt.Start;
            commands.Add(new MoveEventCommand(
                linked.Event, linked.Track, linked.Track, Math.Max(0, linked.Event.Start + delta)));
        }

        _selectedEventId = eventId;
        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand($"Move '{evt.Name}'", commands));
        StatusText = $"Moved '{evt.Name}' to {FormatTime(newStart)}";
    }

    /// <summary>
    /// Time-stretches an event (Shift + edge drag): new timeline span, same
    /// source range, adjusted playback rate. Linked partners that share the
    /// same span are stretched with it so A/V stays in sync.
    /// </summary>
    public void StretchEvent(Guid eventId, double newStart, double newDuration)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;
        var evt = found.Event;
        newDuration = Math.Max(StretchEventCommand.MinDuration, newDuration);
        if (Math.Abs(newStart - evt.Start) < 0.0005 && Math.Abs(newDuration - evt.Duration) < 0.0005) return;

        var stretch = new StretchEventCommand(evt, newStart, newDuration);
        var commands = new List<IEditorCommand> { stretch };

        if (evt.LinkedEventId is Guid linkedId &&
            _projects.Current.FindEvent(linkedId) is { } linked &&
            Math.Abs(linked.Event.Start - evt.Start) < 0.001 &&
            Math.Abs(linked.Event.Duration - evt.Duration) < 0.001)
        {
            commands.Add(new StretchEventCommand(linked.Event, newStart, newDuration));
        }

        _selectedEventId = eventId;
        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand($"Stretch '{evt.Name}'", commands));
        StatusText = $"Stretched '{evt.Name}' to {newDuration:0.##}s ({stretch.NewRate:0.##}x speed)";
    }

    /// <summary>
    /// Breaks the link between an A/V pair so they move independently
    /// (right-click menu or the T key). Undoable.
    /// </summary>
    public void UnlinkEvent(Guid eventId)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;
        var evt = found.Event;
        if (evt.LinkedEventId is not Guid linkedId) return;

        var commands = new List<IEditorCommand>
        {
            new SetValueCommand<Guid?>("Unlink", evt.LinkedEventId, null, v => evt.LinkedEventId = v)
        };
        if (_projects.Current.FindEvent(linkedId) is { } linked)
        {
            var partner = linked.Event;
            commands.Add(new SetValueCommand<Guid?>(
                "Unlink partner", partner.LinkedEventId, null, v => partner.LinkedEventId = v));
        }

        _selectedEventId = eventId;
        _undoRedo.ExecuteCommand(new CompositeCommand($"Unlink audio/video of '{evt.Name}'", commands));
        StatusText = $"Unlinked '{evt.Name}' — the pair now moves independently";
    }

    // ---------- Selection ----------

    public void SelectEvent(Guid? eventId)
    {
        _selectedEventId = eventId;
        foreach (var track in Tracks)
            foreach (var evt in track.Events)
                evt.IsSelected = evt.Id == eventId;
        RefreshSelectionPanels();
        CommandManager.InvalidateRequerySuggested();
    }

    public Guid? SelectedEventId => _selectedEventId;

    private SelectedEventContext? GetSelectedContext()
    {
        if (_selectedEventId is not Guid id) return null;
        if (_projects.Current.FindEvent(id) is not { } found) return null;
        return new SelectedEventContext(found.Event, found.Track, ContentTypeOf(found.Event, found.Track));
    }

    /// <summary>What kind of content an event carries, based on its track and source media.</summary>
    private MediaType ContentTypeOf(TimelineEvent evt, Track track) =>
        track.Type == TrackType.Audio
            ? MediaType.Audio
            : _projects.Current.Media.FindById(evt.MediaId)?.Type ?? MediaType.Video;

    private void RefreshSelectionPanels()
    {
        Effects.RefreshSelection();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedEventName));
        OnPropertyChanged(nameof(SelectedHasVolume));
        OnPropertyChanged(nameof(SelectedVolumePercent));
        OnPropertyChanged(nameof(SelectedVolumeLabel));
    }

    private void DeleteSelected()
    {
        if (_selectedEventId is Guid id) DeleteEvent(id);
    }

    /// <summary>Deletes an event (and its linked partner) — used by Del and the context menu.</summary>
    public void DeleteEvent(Guid id)
    {
        if (_projects.Current.FindEvent(id) is not { } found) return;

        var commands = new List<IEditorCommand> { new RemoveEventCommand(found.Track, found.Event) };
        if (found.Event.LinkedEventId is Guid linkedId &&
            _projects.Current.FindEvent(linkedId) is { } linked)
            commands.Add(new RemoveEventCommand(linked.Track, linked.Event));

        _selectedEventId = null;
        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand($"Delete '{found.Event.Name}'", commands));
        StatusText = $"Deleted '{found.Event.Name}'";
    }

    // ---------- Selected event volume (0–200%) ----------

    private double _volumeEditStart;
    private bool _isEditingVolume;

    public bool HasSelection => _selectedEventId != null;
    public string SelectedEventName => GetSelectedContext()?.Event.Name ?? string.Empty;

    /// <summary>
    /// The event whose volume the panel edits: the selection itself when it is
    /// audio, otherwise its linked audio event (adjusting a video clip's sound).
    /// </summary>
    private TimelineEvent? VolumeTargetEvent
    {
        get
        {
            if (GetSelectedContext() is not { } selected) return null;
            if (selected.ContentType == MediaType.Audio) return selected.Event;
            if (selected.Event.LinkedEventId is Guid linkedId &&
                _projects.Current.FindEvent(linkedId) is { } linked)
                return linked.Event;
            return null;
        }
    }

    public bool SelectedHasVolume => VolumeTargetEvent != null;

    public double SelectedVolumePercent
    {
        get => Math.Round(VolumeLimits.Clamp(VolumeTargetEvent?.Volume ?? VolumeLimits.Default) * 100);
        set
        {
            if (VolumeTargetEvent is not { } target) return;
            var clamped = VolumeLimits.Clamp(value / 100.0);
            if (Math.Abs(target.Volume - clamped) < 0.0001) return;
            target.Volume = clamped; // live while dragging; committed on release
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedVolumeLabel));
        }
    }

    public string SelectedVolumeLabel => $"{SelectedVolumePercent:0}%";

    public void BeginSelectedVolumeEdit()
    {
        if (_isEditingVolume || VolumeTargetEvent is not { } target) return;
        _isEditingVolume = true;
        _volumeEditStart = target.Volume;
    }

    public void EndSelectedVolumeEdit()
    {
        if (!_isEditingVolume) return;
        _isEditingVolume = false;
        if (VolumeTargetEvent is not { } target) return;
        if (Math.Abs(_volumeEditStart - target.Volume) < 0.0001) return;

        var oldValue = _volumeEditStart;
        var newValue = target.Volume;
        target.Volume = oldValue; // the command re-applies it for a clean undo baseline
        _undoRedo.ExecuteCommand(new SetValueCommand<double>(
            $"Set volume of '{target.Name}' to {newValue * 100:0}%",
            oldValue, newValue, v => target.Volume = v));
    }

    // ---------- Effects on events (drop target, fx button, context menu) ----------

    /// <summary>Applies an effect to an event (drag &amp; drop, fx button or context menu).</summary>
    public void ApplyEffectToEvent(string effectId, Guid eventId)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;
        SelectEvent(eventId);
        Effects.ApplyEffect(effectId, found.Event, found.Track, ContentTypeOf(found.Event, found.Track));
    }

    /// <summary>Effects that can attach to the given event (feeds the fx menu).</summary>
    public IReadOnlyList<EffectDefinitionViewModel> GetCompatibleEffects(Guid eventId)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found)
            return Array.Empty<EffectDefinitionViewModel>();

        var contentType = ContentTypeOf(found.Event, found.Track);
        return _catalog.All
            .Where(definition => definition.CanApplyTo(contentType))
            .Select(definition => new EffectDefinitionViewModel(definition))
            .ToList();
    }

    public bool EventHasEffects(Guid eventId) =>
        _projects.Current.FindEvent(eventId)?.Event.Effects.Count > 0;

    /// <summary>Removes the whole effect chain of an event as one undo step.</summary>
    public void RemoveAllEffects(Guid eventId)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;
        if (found.Event.Effects.Count == 0) return;

        var commands = found.Event.Effects
            .Select(instance => (IEditorCommand)new RemoveEffectCommand(
                found.Event.Effects, instance, _catalog.Find(instance.Type)?.Name ?? instance.Type))
            .ToList();

        SelectEvent(eventId);
        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand($"Remove all effects from '{found.Event.Name}'", commands));
        StatusText = $"Removed {commands.Count} effect(s) from '{found.Event.Name}'";
    }

    // ---------- Tracks ----------

    private void ToggleTrackMuted(Guid trackId)
    {
        if (_projects.Current.FindTrack(trackId) is not { } track) return;
        _undoRedo.ExecuteCommand(new SetTrackFlagCommand(track, SetTrackFlagCommand.TrackFlag.Muted, !track.Muted));
    }

    private void ToggleTrackSolo(Guid trackId)
    {
        if (_projects.Current.FindTrack(trackId) is not { } track) return;
        _undoRedo.ExecuteCommand(new SetTrackFlagCommand(track, SetTrackFlagCommand.TrackFlag.Solo, !track.Solo));
    }

    private void CommitTrackVolume(Guid trackId, double oldValue, double newValue)
    {
        if (_projects.Current.FindTrack(trackId) is not { } track) return;
        track.Volume = oldValue; // clean undo baseline; the command applies the new value
        _undoRedo.ExecuteCommand(new SetValueCommand<double>(
            $"Set {track.Name} volume to {newValue * 100:0}%",
            oldValue, newValue, v => track.Volume = v));
    }

    // ---------- Zoom ----------

    public void SetPixelsPerSecond(double value)
    {
        value = Math.Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond);
        if (Math.Abs(value - _pixelsPerSecond) < 0.001) return;
        _pixelsPerSecond = value;
        RebuildFromModel();
        OnPropertyChanged(nameof(PixelsPerSecond));
        OnPropertyChanged(nameof(PlayheadX));
    }

    // ---------- Projections ----------

    private void RebuildFromModel()
    {
        var duration = _projects.Current.Duration;
        TimelineWidth = Math.Max(1200, (duration + 30) * _pixelsPerSecond);
        OnPropertyChanged(nameof(TimelineWidth));

        var callbacks = new TrackCallbacks(ToggleTrackMuted, ToggleTrackSolo, CommitTrackVolume);
        Tracks.Clear();
        foreach (var track in _projects.Current.Tracks)
            Tracks.Add(new TrackViewModel(
                track, _projects.Current, _pixelsPerSecond, _selectedEventId, callbacks, _visuals));

        MediaItems.Clear();
        foreach (var item in _projects.Current.Media.Items)
            MediaItems.Add(new MediaItemViewModel(item, _visuals));

        RebuildRuler();
        RefreshSelectionPanels();
        NotifyRangeChanged();

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(TimelineInfoText));
        OnPropertyChanged(nameof(HistoryText));
        OnPropertyChanged(nameof(UndoToolTip));
        OnPropertyChanged(nameof(RedoToolTip));
    }

    private void RebuildRuler()
    {
        RulerTicks.Clear();
        var candidates = new[] { 0.5, 1, 2, 5, 10, 30, 60, 120, 300 };
        var step = candidates.FirstOrDefault(s => s * _pixelsPerSecond >= 70, 600);
        for (var t = 0.0; t * _pixelsPerSecond <= TimelineWidth; t += step)
            RulerTicks.Add(new RulerTickViewModel(t * _pixelsPerSecond, FormatTime(t)));
    }

    // ---------- Helpers ----------

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        var fraction = Math.Abs(seconds - Math.Round(seconds)) > 0.001;
        if (ts.TotalHours >= 1)
            return ts.ToString(fraction ? @"h\:mm\:ss\.f" : @"h\:mm\:ss");
        return ts.ToString(fraction ? @"m\:ss\.f" : @"m\:ss");
    }

    private static long? TryGetFileSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    private static MediaType DetectMediaType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" => MediaType.Video,
            ".wav" or ".mp3" or ".aac" or ".flac" or ".ogg" => MediaType.Audio,
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".tiff" => MediaType.Image,
            _ => MediaType.Video
        };
    }
}
