using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Services;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Ffmpeg;
using VideoEditor.MediaEngine.Frames;
using VideoEditor.MediaEngine.Playback;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// Drives the preview monitor. Scrubbing renders single frames through the
/// compositor; playback delegates to <see cref="PlaybackEngine"/> (background
/// decoding, frame dropping, wall-clock playhead) while timeline audio plays
/// alongside.
/// </summary>
public class PreviewViewModel : ObservableObject
{
    private const int MaxPreviewWidth = 640;
    private const double PlaybackFps = 24;

    private readonly FrameCompositor _compositor;
    private readonly PlaybackEngine _engine;
    private readonly Func<Project> _getProject;
    private readonly PreviewAudioService? _audio;

    private WriteableBitmap? _bitmap;
    private ImageSource? _currentFrame;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _playbackCts;
    private bool _isPlaying;
    private double _playheadTime;
    private long _renderStamp;

    public PreviewViewModel(
        FrameCompositor compositor,
        FrameExtractor extractor,
        VideoEffectPipeline effects,
        FFmpegLocator locator,
        Func<Project> getProject,
        PreviewAudioService? audio = null)
    {
        _compositor = compositor;
        _engine = new PlaybackEngine(compositor, extractor, effects, locator);
        _getProject = getProject;
        _audio = audio;
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
        if (render && !IsPlaying) RequestRender();
    }

    /// <summary>Re-renders the current frame (after edits, effect changes…).</summary>
    public void RequestRender()
    {
        if (IsPlaying) return; // the play loop owns the screen

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
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        var duration = _getProject().Duration;
        if (duration <= 0.01) return;
        if (_playheadTime >= duration - 0.02) PlayheadTime = 0;

        IsPlaying = true;
        var cts = new CancellationTokenSource();
        _playbackCts = cts;
        _ = RunPlaybackAsync(duration, cts);
    }

    public void Stop()
    {
        StopPlayback();
        Seek(0);
    }

    private void StopPlayback()
    {
        IsPlaying = false;
        _playbackCts?.Cancel();
        _audio?.Stop();
    }

    private async Task RunPlaybackAsync(double duration, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var project = _getProject();
        var (width, height) = PreviewSize(project);
        var origin = _playheadTime;

        // Audio first, then the engine's clock runs in step with it.
        if (_audio != null)
        {
            await _audio.StartAsync(project, origin, token);
            if (!IsPlaying || token.IsCancellationRequested)
            {
                _audio.Stop();
                FinishPlayback(cts);
                return;
            }
        }

        try
        {
            await _engine.RunAsync(
                project, origin, duration, width, height, PlaybackFps,
                onTime: t => PlayheadTime = t,
                present: Present,
                token);
        }
        catch (OperationCanceledException)
        {
            // pause/stop — expected
        }
        catch
        {
            // playback must never crash the app
        }
        finally
        {
            FinishPlayback(cts);
        }
    }

    private void FinishPlayback(CancellationTokenSource cts)
    {
        IsPlaying = false;
        _audio?.Stop();
        if (_playbackCts == cts) _playbackCts = null;
        cts.Dispose();
    }

    // ---------- Single-frame rendering (scrub) ----------

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
        Present(frame.Bgra, frame.Width, frame.Height);
    }

    private void Present(byte[] bgra, int width, int height)
    {
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
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
