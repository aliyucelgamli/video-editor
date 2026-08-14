using VideoEditor.Application.UndoRedo;
using VideoEditor.Domain;

namespace VideoEditor.Application.Services;

/// <summary>Owns the currently open project and its file lifecycle (new/open/save).</summary>
public class ProjectService
{
    private readonly IProjectSerializer _serializer;
    private readonly UndoRedoService _undoRedo;

    public ProjectService(IProjectSerializer serializer, UndoRedoService undoRedo)
    {
        _serializer = serializer;
        _undoRedo = undoRedo;
        _undoRedo.StateChanged += (_, _) => MarkDirty();
        Current = CreateDefaultProject("Untitled Project");
    }

    public Project Current { get; private set; }
    public string? CurrentFilePath { get; private set; }
    public bool IsDirty { get; private set; }
    public string DefaultExtension => _serializer.DefaultExtension;

    /// <summary>Raised when a different project instance is created or loaded.</summary>
    public event EventHandler? ProjectChanged;

    /// <summary>Raised when the dirty flag or file path changes.</summary>
    public event EventHandler? StateChanged;

    public void NewProject(string name = "Untitled Project")
    {
        Current = CreateDefaultProject(name);
        CurrentFilePath = null;
        _undoRedo.Clear();
        IsDirty = false;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Open(string path)
    {
        Current = _serializer.Load(path);
        CurrentFilePath = path;
        _undoRedo.Clear();
        IsDirty = false;
        ProjectChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        if (CurrentFilePath is null)
            throw new InvalidOperationException("Project has no file path yet; use SaveAs.");
        SaveAs(CurrentFilePath);
    }

    public void SaveAs(string path)
    {
        _serializer.Save(Current, path);
        CurrentFilePath = path;
        IsDirty = false;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void MarkDirty()
    {
        if (IsDirty) return;
        IsDirty = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Project CreateDefaultProject(string name) => new()
    {
        Settings = new ProjectSettings { Name = name },
        Tracks =
        {
            new Track { Name = "V1", Type = TrackType.Video },
            new Track { Name = "A1", Type = TrackType.Audio },
            new Track { Name = "T1", Type = TrackType.Overlay }
        }
    };
}
