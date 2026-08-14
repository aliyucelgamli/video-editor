using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
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
    /// <summary>Timeline zoom placeholder — becomes user-controlled with zoom support.</summary>
    public const double PixelsPerSecond = 20.0;

    private readonly UndoRedoService _undoRedo = new();
    private readonly ProjectService _projects;

    private string _statusText = "Ready";

    public MainViewModel()
    {
        _projects = new ProjectService(new JsonProjectSerializer(), _undoRedo);
        _projects.ProjectChanged += (_, _) => OnProjectReplaced();
        _projects.StateChanged += (_, _) => OnPropertyChanged(nameof(WindowTitle));
        _undoRedo.StateChanged += (_, _) => OnHistoryChanged();

        NewProjectCommand = new RelayCommand(NewProject);
        OpenCommand = new RelayCommand(Open);
        SaveCommand = new RelayCommand(Save);
        SaveAsCommand = new RelayCommand(SaveAs);
        ImportMediaCommand = new RelayCommand(ImportMedia);
        UndoCommand = new RelayCommand(() => _undoRedo.Undo(), () => _undoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => _undoRedo.Redo(), () => _undoRedo.CanRedo);
        AddVideoTrackCommand = new RelayCommand(() => AddTrack(TrackType.Video));
        AddAudioTrackCommand = new RelayCommand(() => AddTrack(TrackType.Audio));
        AddOverlayTrackCommand = new RelayCommand(() => AddTrack(TrackType.Overlay));

        OnProjectReplaced();
    }

    public ObservableCollection<TrackViewModel> Tracks { get; } = new();
    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = new();

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

    public string HistoryText => $"Undo: {_undoRedo.UndoCount}   Redo: {_undoRedo.RedoCount}";

    private string ProjectFileFilter =>
        $"Video Editor Project (*{_projects.DefaultExtension})|*{_projects.DefaultExtension}|All Files (*.*)|*.*";

    /// <summary>Returns true when it is safe to discard the current project.</summary>
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

    private void ImportMedia()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Media Files|*.mp4;*.mov;*.avi;*.mkv;*.webm;" +
                     "*.wav;*.mp3;*.aac;*.flac;*.ogg;" +
                     "*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tiff|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        var commands = dialog.FileNames
            .Select(path => new MediaItem
            {
                Name = Path.GetFileName(path),
                FilePath = path,
                Type = DetectMediaType(path),
                FileSizeBytes = TryGetFileSize(path)
            })
            .Select(item => (IEditorCommand)new AddMediaCommand(_projects.Current, item))
            .ToList();

        if (commands.Count == 0) return;

        _undoRedo.ExecuteCommand(commands.Count == 1
            ? commands[0]
            : new CompositeCommand($"Import {commands.Count} files", commands));
        StatusText = $"Imported {commands.Count} file(s)";
    }

    private void AddTrack(TrackType type)
    {
        var prefix = type switch { TrackType.Video => "V", TrackType.Audio => "A", _ => "T" };
        var count = _projects.Current.Tracks.Count(t => t.Type == type);
        var track = new Track { Name = $"{prefix}{count + 1}", Type = type };
        _undoRedo.ExecuteCommand(new AddTrackCommand(_projects.Current, track));
        StatusText = $"Added track {track.Name}";
    }

    private void OnProjectReplaced()
    {
        RebuildFromModel();
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(HistoryText));
    }

    private void OnHistoryChanged()
    {
        // Skeleton approach: rebuild the projections after every model change.
        // Replaced by fine-grained updates + virtualization when the timeline grows.
        RebuildFromModel();
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(HistoryText));
    }

    private void RebuildFromModel()
    {
        Tracks.Clear();
        foreach (var track in _projects.Current.Tracks)
            Tracks.Add(new TrackViewModel(track, PixelsPerSecond));

        MediaItems.Clear();
        foreach (var item in _projects.Current.Media.Items)
            MediaItems.Add(new MediaItemViewModel(item));
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
