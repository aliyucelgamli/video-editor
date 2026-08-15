using VideoEditor.App.Mvvm;

namespace VideoEditor.App.ViewModels;

public enum ExportSessionState
{
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Live state of one export run, shared between <see cref="MainViewModel"/>
/// (which drives the export) and the export progress window (which displays
/// percentage/ETA, offers Cancel, and shows the finished-file actions).
/// All members are used on the UI thread only.
/// </summary>
public class ExportSessionViewModel : ObservableObject
{
    private readonly Action _cancel;
    private double _progress;
    private ExportSessionState _state = ExportSessionState.Running;
    private string _errorMessage = string.Empty;
    private bool _cancelRequested;

    public ExportSessionViewModel(string outputPath, Action cancel)
    {
        OutputPath = outputPath;
        _cancel = cancel;
    }

    /// <summary>Full path of the file being written.</summary>
    public string OutputPath { get; }

    /// <summary>Render progress 0..1.</summary>
    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public ExportSessionState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    /// <summary>Friendly failure description (set when State becomes Failed).</summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    /// <summary>True once the user hit Cancel (the run winds down asynchronously).</summary>
    public bool CancelRequested
    {
        get => _cancelRequested;
        private set => SetProperty(ref _cancelRequested, value);
    }

    public void RequestCancel()
    {
        if (CancelRequested || State != ExportSessionState.Running) return;
        CancelRequested = true;
        _cancel();
    }

    public void MarkCompleted()
    {
        Progress = 1;
        State = ExportSessionState.Completed;
    }

    public void MarkFailed(string message)
    {
        ErrorMessage = message;
        State = ExportSessionState.Failed;
    }

    public void MarkCancelled() => State = ExportSessionState.Cancelled;
}
