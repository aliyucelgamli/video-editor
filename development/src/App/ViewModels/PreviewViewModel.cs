using System.Windows;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoEditor.App.Mvvm;
using VideoEditor.App.Services;
using VideoEditor.App.Ui;
using VideoEditor.Application.Settings;
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
    private const double PlaybackFps = 24;

    private readonly FrameCompositor _compositor;
    private readonly PlaybackEngine _engine;
    private readonly ScrubRenderer _scrub;
    private readonly Func<Project> _getProject;
    private readonly PreviewAudioService? _audio;

    private WriteableBitmap? _bitmap;
    private ImageSource? _currentFrame;
    private CancellationTokenSource? _renderCts;
    private CancellationTokenSource? _playbackCts;
    private bool _isPlaying;
    private bool _isLooping;
    private double _playheadTime;
    private long _renderStamp;

    public PreviewViewModel(
        FrameCompositor compositor,
        FrameExtractor extractor,
        VideoEffectPipeline effects,
        FFmpegLocator locator,
        Func<Project> getProject,
        PreviewAudioService? audio = null,
        Func<EffectPreview?>? effectPreview = null)
    {
        _compositor = compositor;
        _engine = new PlaybackEngine(compositor, extractor, effects, locator);
        _scrub = new ScrubRenderer(locator, compositor);
        _getProject = getProject;
        _audio = audio;
        _effectPreview = effectPreview;
        PlayPauseCommand = new RelayCommand(TogglePlay);
        StopCommand = new RelayCommand(Stop);
        ToggleLoopCommand = new RelayCommand(() => IsLooping = !IsLooping);

        // Short enough to feel instant on a single click, long enough that a
        // scrub drag collapses into one decode.
        _renderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _renderDebounce.Tick += (_, _) => { _renderDebounce.Stop(); RenderNow(); };
    }

    private readonly Func<EffectPreview?>? _effectPreview;
    private readonly DispatcherTimer _renderDebounce;

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleLoopCommand { get; }

    /// <summary>
    /// Width the preview composes at (the monitor scales the result to fit).
    /// Text and graphics are rasterized at this size, so a small canvas makes
    /// titles look soft — but every pixel here is paid on every frame, which
    /// makes this the single biggest playback-speed lever. Driven by the
    /// preview-quality setting; see <see cref="PreviewQuality"/>.
    /// </summary>
    public int PreviewWidth
    {
        get => _previewWidth;
        set => _previewWidth = Math.Clamp(value, 240, 1920);
    }

    private int _previewWidth = PreviewQuality.Normal.Width;

    /// <summary>Repeat the selected range (or the whole project) while playing.</summary>
    public bool IsLooping
    {
        get => _isLooping;
        set => SetProperty(ref _isLooping, value);
    }

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

    public string TimeLabel => TimeText.Compact(_playheadTime);

    /// <summary>Moves the playhead and (optionally) renders the frame there.</summary>
    public void Seek(double time, bool render = true)
    {
        PlayheadTime = Math.Max(0, time);
        if (render && !IsPlaying) RequestRender();
    }

    /// <summary>
    /// Re-renders the current frame (after edits, effect changes…). Requests
    /// are coalesced: dragging the playhead fires dozens of these per second
    /// and every render spawns an ffmpeg process, so only the last one in a
    /// short window actually decodes — that is what keeps scrubbing smooth.
    ///
    /// <paramref name="modelChanged"/> must be true when the timeline itself
    /// changed: the scrub renderer keeps decoders positioned on the old clip
    /// layout and has to drop them.
    /// </summary>
    public void RequestRender(bool modelChanged = false)
    {
        if (modelChanged) _scrub.Invalidate();
        if (IsPlaying) return; // the play loop owns the screen
        _renderDebounce.Stop();
        _renderDebounce.Start();
    }

    private void RenderNow()
    {
        if (IsPlaying) return;

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

        var (start, end) = PlaybackSpan();
        if (end - start <= 0.01) return;
        // Outside the selection (or sitting on its end) → start from its beginning.
        if (_playheadTime < start || _playheadTime >= end - 0.02) PlayheadTime = start;

        // Playback runs its own decoders; the scrub ones would just hold
        // ffmpeg processes open for the whole run.
        _scrub.Invalidate();

        IsPlaying = true;
        var cts = new CancellationTokenSource();
        _playbackCts = cts;
        _ = RunPlaybackAsync(cts);
    }

    /// <summary>
    /// What playback covers: the selected range when one is set (that is what
    /// the loop repeats), the whole project otherwise.
    /// </summary>
    private (double Start, double End) PlaybackSpan()
    {
        var project = _getProject();
        if (project.ExportRange?.Normalized() is { } range && range.Duration > 0.01)
            return (Math.Max(0, range.Start), Math.Min(project.Duration, range.End));
        return (0, project.Duration);
    }

    public void Stop()
    {
        StopPlayback();
        Seek(0);
    }

    /// <summary>
    /// Releases everything holding an ffmpeg process (called when the window
    /// closes). Without this the warm scrub decoders would be orphaned.
    /// </summary>
    public void Shutdown()
    {
        StopPlayback();
        _renderDebounce.Stop();
        _renderCts?.Cancel();
        _scrub.Dispose();
    }

    /// <summary>
    /// Stops playback without moving the playhead — what clicking the timeline
    /// during playback does, so the clicked frame is what you end up looking at.
    /// </summary>
    public void Pause()
    {
        if (!IsPlaying) return;
        StopPlayback();
    }

    private void StopPlayback()
    {
        IsPlaying = false;
        _playbackCts?.Cancel();
        _audio?.Stop();
    }

    private async Task RunPlaybackAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        var project = _getProject();
        var (width, height) = PreviewSize(project);

        try
        {
            // Each pass plays [origin, end); looping starts the next pass at the
            // span's beginning, which also re-seeks the audio.
            do
            {
                var (start, end) = PlaybackSpan();
                var origin = _playheadTime < start || _playheadTime >= end ? start : _playheadTime;

                if (_audio != null)
                {
                    await _audio.StartAsync(project, origin, token);
                    if (!IsPlaying || token.IsCancellationRequested) break;
                }

                await _engine.RunAsync(
                    project, origin, end, width, height, PlaybackFps,
                    onTime: t => PlayheadTime = t,
                    present: Present,
                    token,
                    _effectPreview);

                if (!IsLooping || token.IsCancellationRequested) break;
                _audio?.Stop();
                PlayheadTime = start;
            }
            while (IsPlaying && !token.IsCancellationRequested);
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
            // Not ComposeAsync directly: the scrub renderer reuses decoders it
            // primed at the previous position, so dragging the playhead forward
            // costs a few ms per frame instead of a seek+decode per layer.
            frame = await _scrub.RenderAsync(
                project, time, width, height, cancellationToken, _effectPreview?.Invoke());
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

    private (int Width, int Height) PreviewSize(Project project) =>
        FrameSizes.FitWithin(project.Settings.Width, project.Settings.Height, PreviewWidth);
}
