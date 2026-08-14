using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.App.Mvvm;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// Drives the preview monitor: renders the composed frame at the playhead and
/// runs the play loop. Rendering is throttled — a newer request cancels the
/// one in flight, so scrubbing stays responsive.
/// </summary>
public class PreviewViewModel : ObservableObject
{
    private const int MaxPreviewWidth = 640;

    private readonly FrameCompositor _compositor;
    private readonly Func<Project> _getProject;

    private WriteableBitmap? _bitmap;
    private ImageSource? _currentFrame;
    private CancellationTokenSource? _renderCts;
    private bool _isPlaying;
    private double _playheadTime;
    private long _renderStamp;

    public PreviewViewModel(FrameCompositor compositor, Func<Project> getProject)
    {
        _compositor = compositor;
        _getProject = getProject;
        PlayPauseCommand = new RelayCommand(TogglePlay);
        StopCommand = new RelayCommand(Stop);
    }

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }

    public ImageSource? CurrentFrame
    {
        get => _currentFrame;
        private set => SetProperty(ref _currentFrame, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
                OnPropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    /// <summary>Segoe MDL2: play / pause.</summary>
    public string PlayPauseGlyph => _isPlaying ? "\uE769" : "\uE768";

    public double PlayheadTime
    {
        get => _playheadTime;
        private set
        {
            if (SetProperty(ref _playheadTime, value))
                OnPropertyChanged(nameof(TimeLabel));
        }
    }

    public string TimeLabel
    {
        get
        {
            var ts = TimeSpan.FromSeconds(Math.Max(0, _playheadTime));
            return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss\.f") : ts.ToString(@"m\:ss\.f");
        }
    }

    /// <summary>Moves the playhead and (optionally) renders the frame there.</summary>
    public void Seek(double time, bool render = true)
    {
        PlayheadTime = Math.Max(0, time);
        if (render) RequestRender();
    }

    /// <summary>Re-renders the current frame (after edits, effect changes…).</summary>
    public void RequestRender()
    {
        var project = _getProject();
        var (width, height) = PreviewSize(project);
        var time = _playheadTime;
        var stamp = ++_renderStamp;

        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;

        _ = RenderAsync(project, time, width, height, stamp, cts.Token);
    }

    public void TogglePlay()
    {
        if (IsPlaying) { IsPlaying = false; return; }

        var duration = _getProject().Duration;
        if (duration <= 0.01) return;
        if (_playheadTime >= duration - 0.02) PlayheadTime = 0;

        IsPlaying = true;
        _ = RunPlaybackAsync(duration);
    }

    public void Stop()
    {
        IsPlaying = false;
        Seek(0);
    }

    private async Task RunPlaybackAsync(double duration)
    {
        var project = _getProject();
        var (width, height) = PreviewSize(project);
        var origin = _playheadTime;
        var clock = Stopwatch.StartNew();

        while (IsPlaying)
        {
            var time = origin + clock.Elapsed.TotalSeconds;
            if (time >= duration)
            {
                PlayheadTime = duration;
                IsPlaying = false;
                break;
            }

            PlayheadTime = time;
            var stamp = ++_renderStamp;
            try
            {
                // Sequential await = natural frame pacing at decode speed.
                await RenderAsync(project, time, width, height, stamp, CancellationToken.None);
            }
            catch
            {
                IsPlaying = false;
                break;
            }
        }
    }

    private async Task RenderAsync(
        Project project, double time, int width, int height, long stamp, CancellationToken cancellationToken)
    {
        RawFrame frame;
        try
        {
            frame = await _compositor.ComposeAsync(project, time, width, height, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return; // a missing file or dying ffmpeg must never break the UI
        }

        if (stamp != _renderStamp) return; // a newer frame is already on its way

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Width * 4, 0);
        CurrentFrame = _bitmap;
    }

    private static (int Width, int Height) PreviewSize(Project project)
    {
        var settings = project.Settings;
        var aspect = settings.Width > 0 && settings.Height > 0
            ? (double)settings.Height / settings.Width
            : 9.0 / 16.0;
        var width = Math.Min(MaxPreviewWidth, Math.Max(64, settings.Width));
        var height = (int)Math.Round(width * aspect);
        return (width - width % 2, Math.Max(2, height - height % 2));
    }
}
