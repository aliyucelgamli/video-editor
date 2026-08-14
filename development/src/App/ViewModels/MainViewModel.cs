using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using VideoEditor.App.Mvvm;
using VideoEditor.Application.Commands;
using VideoEditor.Application.Services;
using VideoEditor.Application.UndoRedo;
using VideoEditor.Domain;
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

    private string _statusText = "Ready — drop media files into the library or a track";
    private double _pixelsPerSecond = 20.0;
    private Guid? _selectedEventId;

    /// <summary>Raised when the view should zoom the timeline by a factor (anchored in view code).</summary>
    public event EventHandler<double>? ZoomRequested;

    public MainViewModel()
    {
        _projects = new ProjectService(new JsonProjectSerializer(), _undoRedo);
        _projects.ProjectChanged += (_, _) => { _selectedEventId = null; RebuildFromModel(); };
        _projects.StateChanged += (_, _) => OnPropertyChanged(nameof(WindowTitle));
        _undoRedo.StateChanged += (_, _) => RebuildFromModel();

        NewProjectCommand = new RelayCommand(NewProject);
        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(Save);
        SaveAsCommand = new RelayCommand(SaveAs);
        ImportMediaCommand = new RelayCommand(ImportMediaDialog);
        UndoCommand = new RelayCommand(() => _undoRedo.Undo(), () => _undoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => _undoRedo.Redo(), () => _undoRedo.CanRedo);
        AddVideoTrackCommand = new RelayCommand(() => AddTrack(TrackType.Video));
        AddAudioTrackCommand = new RelayCommand(() => AddTrack(TrackType.Audio));
        AddOverlayTrackCommand = new RelayCommand(() => AddTrack(TrackType.Overlay));
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => _selectedEventId is not null);
        ZoomInCommand = new RelayCommand(() => ZoomRequested?.Invoke(this, 1.25));
        ZoomOutCommand = new RelayCommand(() => ZoomRequested?.Invoke(this, 0.8));

        RebuildFromModel();
    }

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
    public RelayCommand AddVideoTrackCommand { get; }
    public RelayCommand AddAudioTrackCommand { get; }
    public RelayCommand AddOverlayTrackCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }

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
                     "*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tiff|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        ImportFiles(dialog.FileNames);
    }

    /// <summary>Imports files into the media library (used by the Import button and Explorer drops).</summary>
    public void ImportFiles(IEnumerable<string> paths)
    {
        var items = BuildImportItems(paths, out var skipped);
        if (items.Count == 0)
        {
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

        StatusText = skipped > 0
            ? $"Imported {items.Count} file(s), skipped {skipped}"
            : $"Imported {items.Count} file(s)";
    }

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

        var evt = CreateEvent(media, time);
        _selectedEventId = evt.Id;
        _undoRedo.ExecuteCommand(new AddEventCommand(track, evt));
        StatusText = $"Placed '{media.Name}' at {FormatTime(evt.Start)}";
    }

    /// <summary>Drops Explorer files straight onto a track: imports (if new) and places compatible clips.</summary>
    public void DropFilesOnTrack(Guid trackId, IEnumerable<string> paths, double time)
    {
        var track = _projects.Current.FindTrack(trackId);
        if (track is null) return;

        var commands = new List<IEditorCommand>();
        var cursor = Math.Max(0, time);
        int placed = 0, imported = 0, skipped = 0;
        Guid? lastEventId = null;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
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
                imported++;
            }

            if (IsCompatible(item.Type, track.Type))
            {
                var evt = CreateEvent(item, cursor);
                commands.Add(new AddEventCommand(track, evt));
                cursor = evt.End;
                placed++;
                lastEventId = evt.Id;
            }
        }

        if (commands.Count == 0)
        {
            StatusText = skipped > 0 ? $"Skipped {skipped} unsupported file(s)" : "Nothing to add";
            return;
        }

        if (lastEventId is Guid id) _selectedEventId = id;
        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand("Add media to timeline", commands));

        var parts = new List<string>();
        if (placed > 0) parts.Add($"placed {placed} clip(s)");
        if (imported > 0) parts.Add($"imported {imported} file(s)");
        if (skipped > 0) parts.Add($"skipped {skipped}");
        StatusText = char.ToUpper(parts[0][0]) + string.Join(", ", parts)[1..];
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

    // ---------- Selection ----------

    public void SelectEvent(Guid? eventId)
    {
        _selectedEventId = eventId;
        foreach (var track in Tracks)
            foreach (var evt in track.Events)
                evt.IsSelected = evt.Id == eventId;
        CommandManager.InvalidateRequerySuggested();
    }

    private void DeleteSelected()
    {
        if (_selectedEventId is not Guid id) return;
        var found = _projects.Current.FindEvent(id);
        if (found is null) return;

        _selectedEventId = null;
        _undoRedo.ExecuteCommand(new RemoveEventCommand(found.Value.Track, found.Value.Event));
        StatusText = $"Deleted '{found.Value.Event.Name}'";
    }

    // ---------- Tracks ----------

    private void AddTrack(TrackType type)
    {
        var prefix = type switch { TrackType.Video => "V", TrackType.Audio => "A", _ => "T" };
        var count = _projects.Current.Tracks.Count(t => t.Type == type);
        var track = new Track { Name = $"{prefix}{count + 1}", Type = type };
        _undoRedo.ExecuteCommand(new AddTrackCommand(_projects.Current, track));
        StatusText = $"Added track {track.Name}";
    }

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

    // ---------- Zoom ----------

    public void SetPixelsPerSecond(double value)
    {
        value = Math.Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond);
        if (Math.Abs(value - _pixelsPerSecond) < 0.001) return;
        _pixelsPerSecond = value;
        RebuildFromModel();
        OnPropertyChanged(nameof(PixelsPerSecond));
    }

    // ---------- Projections ----------

    private void RebuildFromModel()
    {
        var duration = _projects.Current.Duration;
        TimelineWidth = Math.Max(1200, (duration + 30) * _pixelsPerSecond);
        OnPropertyChanged(nameof(TimelineWidth));

        Tracks.Clear();
        foreach (var track in _projects.Current.Tracks)
            Tracks.Add(new TrackViewModel(track, _pixelsPerSecond, _selectedEventId, ToggleTrackMuted, ToggleTrackSolo));

        MediaItems.Clear();
        foreach (var item in _projects.Current.Media.Items)
            MediaItems.Add(new MediaItemViewModel(item));

        RebuildRuler();

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
