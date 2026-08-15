using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Ui;
using VideoEditor.App.Services;
using VideoEditor.Application.Commands;
using VideoEditor.Application.Editing;
using VideoEditor.Application.Actions;
using VideoEditor.Application.Settings;
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
    private readonly FrameExtractor _frameExtractor;
    private readonly SettingsService _settingsService;
    private readonly TextRasterizerService _textRasterizer;
    private readonly TextRasterCache _textRasters;
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

        _settingsService = new SettingsService(Path.Combine(appRoot, "user"));
        Settings = _settingsService.Load();
        Shortcuts = new ShortcutMap(Settings.Shortcuts);

        _frameExtractor = new FrameExtractor(_ffmpeg);
        var effectPipeline = new VideoEffectPipeline(_catalog);
        var compositor = new FrameCompositor(_frameExtractor, effectPipeline);
        _textRasterizer = new TextRasterizerService(compositor.TextRasters);
        _textRasters = compositor.TextRasters;
        _exporter = new ExportService(_ffmpeg, compositor, _catalog);

        var previewAudio = new PreviewAudioService(_ffmpeg, cache, _catalog);
        Preview = new PreviewViewModel(
            compositor, _frameExtractor, effectPipeline, _ffmpeg, () => _projects.Current, previewAudio);
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
        SplitAtPlayheadCommand = new RelayCommand(SplitAtPlayhead);
        AddTextCommand = new RelayCommand(() => AddTextRequested?.Invoke(this, EventArgs.Empty));
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

    /// <summary>Ffmpeg executable path for view-side helpers (GPU encoder probe).</summary>
    public string? FfmpegPath => _ffmpeg.FfmpegPath;

    /// <summary>User preferences (user/settings.json).</summary>
    public AppSettings Settings { get; }

    /// <summary>Live keyboard map; the window turns it into input bindings.</summary>
    public ShortcutMap Shortcuts { get; }

    /// <summary>Persists Settings including the current shortcut overrides.</summary>
    public void SaveSettings()
    {
        Settings.Shortcuts = Shortcuts.ToOverrides();
        _settingsService.Save(Settings);
    }

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
    public RelayCommand SplitAtPlayheadCommand { get; }
    public RelayCommand AddTextCommand { get; }
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

    /// <summary>Raised when the export dialog should open (handled by the window).</summary>
    public event EventHandler? ExportRequested;

    public Project CurrentProject => _projects.Current;

    private void Export()
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

        ExportRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised right after an export run starts, with its live session state.
    /// The window opens the modal progress dialog for it.
    /// </summary>
    public event EventHandler<ExportSessionViewModel>? ExportSessionStarted;

    /// <summary>Runs an export with the options confirmed in the export dialog.</summary>
    public async void StartExport(
        ExportFormat format, int width, int height, double fps, int crf, bool useHardwareEncoder)
    {
        var exportDir = Settings.DefaultExportFolder is { Length: > 0 } configured
            ? configured
            : Path.Combine(CachePaths.LocateAppRoot(), "user", "exports");
        try { Directory.CreateDirectory(exportDir); } catch { exportDir = string.Empty; }

        var dialog = new SaveFileDialog
        {
            Filter = format.SaveDialogFilter(),
            FileName = _projects.Current.Settings.Name + format.Extension(),
            InitialDirectory = exportDir
        };
        if (dialog.ShowDialog() != true) return;

        var settings = ExportSettings.FromProject(_projects.Current, dialog.FileName);
        settings.Format = format;
        settings.Width = width;
        settings.Height = height;
        settings.FrameRate = fps;
        settings.Crf = crf;
        settings.UseHardwareEncoder = useHardwareEncoder;
        var rangeText = settings.Range != null ? " (selected range)" : " (whole project)";

        // Text layers are WPF-rendered on the UI thread; warm the cache at the
        // export size so the render pipeline finds them ready.
        PreRenderTextRasters(width, height);

        IsExporting = true;
        ExportProgress = 0;
        _exportCts = new CancellationTokenSource();
        var session = new ExportSessionViewModel(dialog.FileName, () => _exportCts?.Cancel());
        ExportSessionStarted?.Invoke(this, session);
        var progress = new Progress<double>(p =>
        {
            ExportProgress = p;
            session.Progress = p;
            StatusText = $"Exporting{rangeText}… {p:P0}";
        });

        try
        {
            await _exporter.ExportAsync(_projects.Current, settings, progress, _exportCts.Token);
            session.MarkCompleted();
            StatusText = $"Exported to {Path.GetFileName(dialog.FileName)}";
        }
        catch (OperationCanceledException)
        {
            session.MarkCancelled();
            StatusText = "Export cancelled";
            TryDelete(dialog.FileName);
        }
        catch (Exception ex)
        {
            // The progress window presents the failure; no MessageBox needed.
            session.MarkFailed(ex.Message);
            StatusText = "Export failed";
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

    /// <summary>Raised when the New Project dialog should open (handled by the window).</summary>
    public event EventHandler? NewProjectRequested;

    private void NewProject() => NewProjectRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Creates a fresh project with the settings chosen in the New Project dialog.</summary>
    public void CreateNewProject(string name, int width, int height, double fps)
    {
        _projects.NewProject(string.IsNullOrWhiteSpace(name) ? "Untitled Project" : name.Trim());
        var settings = _projects.Current.Settings;
        settings.Width = width;
        settings.Height = height;
        settings.FrameRate = fps;

        Preview.Seek(0, render: false);
        RebuildFromModel();
        StatusText = $"New project — {width}×{height} @ {fps:0.##} fps";
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
        // Drops are re-routed to a compatible track, so any drop is accepted
        // as long as one exists somewhere.
        var media = _projects.Current.Media.FindById(mediaId);
        return media != null &&
               _projects.Current.Tracks.Any(t => IsCompatible(media.Type, t.Type));
    }

    /// <summary>
    /// Assets land where they belong regardless of the lane they were dropped
    /// on: video/images go to a visual track, audio to an audio track.
    /// </summary>
    private Track? RouteToCompatibleTrack(MediaItem media, Track dropTarget) =>
        IsCompatible(media.Type, dropTarget.Type)
            ? dropTarget
            : _projects.Current.Tracks.FirstOrDefault(t => IsCompatible(media.Type, t.Type));

    public void DropMediaOnTrack(Guid mediaId, Guid trackId, double time)
    {
        var media = _projects.Current.Media.FindById(mediaId);
        var dropTarget = _projects.Current.FindTrack(trackId);
        if (media is null || dropTarget is null) return;

        if (RouteToCompatibleTrack(media, dropTarget) is not { } track)
        {
            StatusText = $"No track can hold a {media.Type} clip";
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

    /// <summary>Drops Explorer files straight onto a track: imports (if new) and places clips on compatible tracks.</summary>
    public void DropFilesOnTrack(Guid trackId, IEnumerable<string> paths, double time)
    {
        var dropTarget = _projects.Current.FindTrack(trackId);
        if (dropTarget is null) return;

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

            if (RouteToCompatibleTrack(item, dropTarget) is { } track)
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
    /// Snaps a yellow range bar to nearby clip edges, the playhead or zero —
    /// dragging a bar onto a clip boundary is how you export exactly one clip.
    /// </summary>
    public double SnapBarTime(double time)
    {
        var threshold = 8.0 / _pixelsPerSecond;
        var best = Math.Max(0, time);
        var bestDistance = threshold;

        var candidates = _projects.Current.Tracks
            .SelectMany(t => t.Events)
            .SelectMany(e => new[] { e.Start, e.End })
            .Append(0)
            .Append(Preview.PlayheadTime);

        foreach (var point in candidates)
        {
            var distance = Math.Abs(point - time);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = point;
            }
        }
        return Math.Max(0, best);
    }

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

    /// <summary>Runs an undoable command from auxiliary windows (clip properties…).</summary>
    public void RunCommand(IEditorCommand command) => _undoRedo.ExecuteCommand(command);

    // ---------- Split at playhead (S / X) ----------

    /// <summary>
    /// Splits at the playhead: the selected clip (with its linked partner)
    /// when one is selected, otherwise every clip under the playhead.
    /// </summary>
    private void SplitAtPlayhead()
    {
        var time = Preview.PlayheadTime;
        var targets = CollectSplitTargets(time);
        if (targets.Count == 0)
        {
            StatusText = "Nothing to split at the playhead";
            return;
        }

        var commands = targets
            .Select(t => (IEditorCommand)new SplitEventCommand(t.Track, t.Event, time))
            .ToList();
        RunCommand(commands.Count == 1 ? commands[0] : new CompositeCommand("Split at playhead", commands));
        StatusText = $"Split {targets.Count} clip(s) at {FormatTime(time)}";
        RequestPreviewRefresh();
    }

    private List<(Track Track, TimelineEvent Event)> CollectSplitTargets(double time)
    {
        const double margin = 0.02; // splitting razor-thin slivers helps nobody
        var project = _projects.Current;
        bool Splittable(TimelineEvent evt) => time > evt.Start + margin && time < evt.End - margin;

        var targets = new List<(Track, TimelineEvent)>();
        if (_selectedEventId is Guid id &&
            project.FindEvent(id) is { } selected && Splittable(selected.Event))
        {
            targets.Add(selected);
            if (selected.Event.LinkedEventId is Guid linkedId &&
                project.FindEvent(linkedId) is { } linked && Splittable(linked.Event))
                targets.Add(linked);
            return targets;
        }

        foreach (var track in project.Tracks)
            foreach (var evt in track.Events.Where(Splittable))
                targets.Add((track, evt));
        return targets;
    }

    // ---------- Text (title) events ----------

    /// <summary>Raised when the Add Text dialog should open (handled by the window).</summary>
    public event EventHandler? AddTextRequested;

    /// <summary>Places a new title at the playhead on the overlay track (created on demand).</summary>
    public void AddTextEvent(TextStyle style)
    {
        var project = _projects.Current;
        var track = project.Tracks.FirstOrDefault(t => t.Type == TrackType.Overlay);
        var commands = new List<IEditorCommand>();
        if (track is null)
        {
            track = new Track { Name = "T1", Type = TrackType.Overlay };
            commands.Add(new AddTrackCommand(project, track, 0)); // top lane renders above
        }

        var evt = new TimelineEvent
        {
            Name = TextEventName(style),
            Start = Preview.PlayheadTime,
            Duration = DefaultEventDuration,
            SourceOut = DefaultEventDuration,
            Text = style
        };
        commands.Add(new AddEventCommand(track, evt));

        RasterizeTextStyle(style);
        RunCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand($"Add text '{evt.Name}'", commands));
        SelectEvent(evt.Id);
        RequestPreviewRefresh();
        StatusText = $"Text added at {FormatTime(evt.Start)} — use the clip's size button to place it";
    }

    public void EditTextEvent(Guid eventId, TextStyle style)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found ||
            found.Event.Text is not { } oldStyle) return;

        var evt = found.Event;
        RasterizeTextStyle(style);
        RunCommand(new CompositeCommand($"Edit text '{evt.Name}'", new List<IEditorCommand>
        {
            new SetValueCommand<TextStyle?>("Text style", oldStyle, style, v => evt.Text = v),
            new SetValueCommand<string>("Text name", evt.Name, TextEventName(style), v => evt.Name = v)
        }));
        RequestPreviewRefresh();
    }

    public TextStyle? GetTextStyle(Guid eventId) =>
        _projects.Current.FindEvent(eventId)?.Event.Text;

    private static string TextEventName(TextStyle style)
    {
        var firstLine = style.Content.Split('\n')[0].Trim();
        return firstLine.Length <= 24 ? firstLine : firstLine[..24] + "…";
    }

    /// <summary>Rasterizes a style at preview size (UI thread; cache makes repeats free).</summary>
    private void RasterizeTextStyle(TextStyle style)
    {
        var settings = _projects.Current.Settings;
        var (width, height) = FrameSizes.FitWithin(
            settings.Width, settings.Height, PreviewViewModel.MaxPreviewWidth);
        _textRasterizer.EnsureRendered(style, width, height, settings.Width);
    }

    /// <summary>Loaded projects bring text events with them — keep rasters warm.</summary>
    private void RasterizeAllTextEvents()
    {
        foreach (var track in _projects.Current.Tracks)
            foreach (var evt in track.Events)
                if (evt.Text is { } style)
                    RasterizeTextStyle(style);
    }

    private void PreRenderTextRasters(int width, int height)
    {
        foreach (var track in _projects.Current.Tracks)
            foreach (var evt in track.Events)
                if (evt.Text is { } style)
                    _textRasterizer.EnsureRendered(style, width, height, _projects.Current.Settings.Width);
    }

    // ---------- Trim (plain edge drag) and slip (Alt+drag) ----------

    /// <summary>
    /// Trims one edge to the given timeline geometry, keeping the playback
    /// rate fixed (the source range follows). Clamped to the media's bounds.
    /// A linked partner is trimmed with the same timeline edge.
    /// </summary>
    public void TrimEvent(Guid eventId, bool fromLeftEdge, double newStart, double newDuration)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;

        var commands = new List<IEditorCommand> { BuildTrim(found.Event, fromLeftEdge, newStart, newDuration) };
        if (found.Event.LinkedEventId is Guid linkedId &&
            _projects.Current.FindEvent(linkedId) is { } linked)
            commands.Add(BuildTrim(linked.Event, fromLeftEdge, newStart, newDuration));

        RunCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand(commands[0].Description, commands));
    }

    private IEditorCommand BuildTrim(TimelineEvent evt, bool fromLeftEdge, double newStart, double newDuration) =>
        EdgeTrim.BuildTrim(
            evt, _projects.Current.Media.FindById(evt.MediaId)?.DurationSeconds,
            fromLeftEdge, newStart, newDuration);

    /// <summary>
    /// Slips the source range by a timeline delta (drag right = show earlier
    /// footage), clamped to the media bounds. Linked partners slip together.
    /// </summary>
    public void SlipEvent(Guid eventId, double deltaSeconds)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;

        var commands = new List<IEditorCommand>();
        AddSlip(commands, found.Event, deltaSeconds);
        if (found.Event.LinkedEventId is Guid linkedId &&
            _projects.Current.FindEvent(linkedId) is { } linked)
            AddSlip(commands, linked.Event, deltaSeconds);

        if (commands.Count == 0)
        {
            StatusText = "Nothing to slip — the clip already shows its full source";
            return;
        }
        RunCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand(commands[0].Description, commands));
    }

    private void AddSlip(List<IEditorCommand> commands, TimelineEvent evt, double deltaSeconds)
    {
        if (EdgeTrim.BuildSlip(
                evt, _projects.Current.Media.FindById(evt.MediaId)?.DurationSeconds, deltaSeconds)
            is { } slip)
            commands.Add(slip);
    }

    // ---------- Fades (corner grips on clips) ----------

    /// <summary>Live update while a fade grip is dragged — no undo entry yet.</summary>
    public void SetEventFadeLive(Guid eventId, bool fadeIn, double seconds)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;
        var evt = found.Event;
        seconds = Math.Clamp(seconds, 0, evt.Duration);
        if (fadeIn) evt.FadeInDuration = seconds;
        else evt.FadeOutDuration = seconds;
        StatusText = $"{(fadeIn ? "Fade in" : "Fade out")}: {seconds:0.##}s";
        RequestPreviewRefresh();
    }

    /// <summary>One undoable command per grip drag (issued on mouse release).</summary>
    public void CommitEventFade(Guid eventId, bool fadeIn, double originalSeconds)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;
        var evt = found.Event;
        var newValue = fadeIn ? evt.FadeInDuration : evt.FadeOutDuration;
        if (Math.Abs(newValue - originalSeconds) < 0.001) return;

        Action<double> set;
        if (fadeIn) set = v => evt.FadeInDuration = v;
        else set = v => evt.FadeOutDuration = v;

        set(originalSeconds); // rewind so undo lands exactly where the drag started
        RunCommand(new SetValueCommand<double>(
            fadeIn ? "Set fade in" : "Set fade out", originalSeconds, newValue, set));
    }

    public void SetEventFadeEasing(Guid eventId, bool fadeIn, EasingType easing)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return;
        var evt = found.Event;
        var old = fadeIn ? evt.FadeInEasing : evt.FadeOutEasing;
        if (old == easing) return;

        RunCommand(fadeIn
            ? new SetValueCommand<EasingType>("Set fade in easing", old, easing, v => evt.FadeInEasing = v)
            : new SetValueCommand<EasingType>("Set fade out easing", old, easing, v => evt.FadeOutEasing = v));
        RequestPreviewRefresh();
    }

    /// <summary>Current fade state of a clip (feeds the grip drag and the easing menu).</summary>
    public (double FadeIn, double FadeOut, EasingType InEasing, EasingType OutEasing)? GetEventFadeInfo(Guid eventId) =>
        _projects.Current.FindEvent(eventId) is { } found
            ? (found.Event.FadeInDuration, found.Event.FadeOutDuration,
                found.Event.FadeInEasing, found.Event.FadeOutEasing)
            : null;

    /// <summary>Builds the view model for the Clip Properties window (size/…/info buttons).</summary>
    public EventPropertiesViewModel? CreateEventProperties(Guid eventId)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return null;
        var (track, evt) = found;

        var media = _projects.Current.Media.FindById(evt.MediaId);
        var volumeTarget = ContentTypeOf(evt, track) == MediaType.Audio
            ? evt
            : evt.LinkedEventId is Guid linkedId && _projects.Current.FindEvent(linkedId) is { } linked
                ? linked.Event
                : null;

        return new EventPropertiesViewModel(
            evt, track, media, volumeTarget, _projects.Current.Settings,
            RunCommand, RequestPreviewRefresh);
    }

    /// <summary>
    /// Builds the visual transform editor for a clip; null for audio clips
    /// (which have nothing to scale — the caller falls back to Properties).
    /// The stage shows the frame under the playhead when it is inside the
    /// clip, the clip's middle frame otherwise.
    /// </summary>
    public TransformEditorViewModel? CreateTransformEditor(Guid eventId)
    {
        if (_projects.Current.FindEvent(eventId) is not { } found) return null;
        var (track, evt) = found;
        if (ContentTypeOf(evt, track) == MediaType.Audio) return null;

        var media = _projects.Current.Media.FindById(evt.MediaId);
        var playhead = Preview.PlayheadTime;
        var sourceTime = media?.Type == MediaType.Image ? 0
            : evt.Contains(playhead) ? evt.SourceIn + (playhead - evt.Start) * evt.PlaybackRate
            : evt.SourceIn + Math.Max(0, evt.SourceOut - evt.SourceIn) / 2;

        RawFrame? stageFrame = null;
        if (evt.Text is { } style)
        {
            var settings = _projects.Current.Settings;
            var (width, height) = FrameSizes.FitWithin(settings.Width, settings.Height, 720);
            _textRasterizer.EnsureRendered(style, width, height, settings.Width);
            stageFrame = _textRasters.TryGetShared(style, width, height);
        }

        return new TransformEditorViewModel(
            evt, media, _projects.Current.Settings, _frameExtractor, sourceTime,
            RunCommand, RequestPreviewRefresh, stageFrame);
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

    private void CommitTrackOpacity(Guid trackId, double oldValue, double newValue)
    {
        if (_projects.Current.FindTrack(trackId) is not { } track) return;
        track.Opacity = oldValue; // clean undo baseline; the command applies the new value
        _undoRedo.ExecuteCommand(new SetValueCommand<double>(
            $"Set {track.Name} opacity to {newValue * 100:0}%",
            oldValue, newValue, v => track.Opacity = v));
        RequestPreviewRefresh();
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

        var callbacks = new TrackCallbacks(ToggleTrackMuted, ToggleTrackSolo, CommitTrackVolume, CommitTrackOpacity);
        Tracks.Clear();
        foreach (var track in _projects.Current.Tracks)
            Tracks.Add(new TrackViewModel(
                track, _projects.Current, _pixelsPerSecond, _selectedEventId, callbacks, _visuals));

        MediaItems.Clear();
        foreach (var item in _projects.Current.Media.Items)
            MediaItems.Add(new MediaItemViewModel(item, _visuals));

        RasterizeAllTextEvents();
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

    private static string FormatTime(double seconds) => TimeText.Compact(seconds);

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
