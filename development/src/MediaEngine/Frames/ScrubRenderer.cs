using VideoEditor.Domain;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>
/// Renders the frames the preview shows while the user is NOT playing: clicking
/// the ruler, dragging the playhead, stepping frame by frame.
///
/// A cold position costs one ffmpeg seek+decode per layer (100+ ms each —
/// ffmpeg cannot be asked to seek on demand, so that first frame is
/// unavoidable). What this class adds is anticipation: after every cold frame
/// it primes sequential decoders just past that point in the background, so a
/// request slightly AHEAD is served by reading the running processes instead of
/// starting new ones. Dragging the playhead forward is exactly that pattern,
/// which turns a chain of ~350 ms decodes into ~3 ms reads.
///
/// One render at a time (preview requests are debounced and supersede each
/// other); the sequential decoders cannot be shared across concurrent renders.
/// </summary>
public sealed class ScrubRenderer : IDisposable
{
    /// <summary>
    /// How far ahead a primed session may be caught up to by decoding frames.
    /// Beyond this, restarting the decoders is cheaper than reading through.
    /// </summary>
    private const double MaxCatchUpSeconds = 1.0;

    /// <summary>Frame grid the primed session advances on (independent of project fps).</summary>
    private const double ScrubFps = 30;

    private readonly FFmpegLocator _locator;
    private readonly FrameCompositor _compositor;

    private Session? _session;
    private int _width;
    private int _height;

    public ScrubRenderer(FFmpegLocator locator, FrameCompositor compositor)
    {
        _locator = locator;
        _compositor = compositor;
    }

    /// <summary>True when the last frame came from primed decoders (diagnostics).</summary>
    public bool LastFrameWasWarm { get; private set; }

    /// <summary>
    /// Renders the timeline at <paramref name="time"/>. Never throws for media
    /// reasons — a broken decoder falls back to the per-frame compositor.
    /// </summary>
    public async Task<RawFrame> RenderAsync(
        Project project, double time, int width, int height,
        CancellationToken cancellationToken, EffectPreview? preview = null)
    {
        width -= width % 2;
        height -= height % 2;
        if (width != _width || height != _height)
        {
            DropSession();
            _width = width;
            _height = height;
        }

        // Cheap check first (no waiting): could this session possibly cover the
        // request? Only then is it worth waiting for its priming decode.
        if (_session is { } session && session.MightReach(time, MaxCatchUpSeconds))
        {
            try
            {
                if (await session.WaitUntilPrimedAsync(cancellationToken).ConfigureAwait(false) &&
                    session.Reaches(time, ScrubFps, MaxCatchUpSeconds))
                {
                    var warm = await session
                        .AdvanceToAsync(project, time, ScrubFps, cancellationToken, preview)
                        .ConfigureAwait(false);
                    LastFrameWasWarm = true;
                    return new RawFrame(warm, width, height);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                DropSession(); // a dead decoder must not poison later frames
            }
        }

        LastFrameWasWarm = false;
        var frame = await _compositor
            .ComposeAsync(project, time, width, height, cancellationToken, preview)
            .ConfigureAwait(false);

        PrimeSession(project, time, preview);
        return frame;
    }

    /// <summary>
    /// Drops the primed decoders. Must be called whenever the model changes
    /// under the playhead — the decoders are bound to clip positions and would
    /// otherwise hand back frames from before the edit.
    /// </summary>
    public void Invalidate() => DropSession();

    public void Dispose() => DropSession();

    // ---------- Session lifetime ----------

    /// <summary>
    /// Starts decoders at <paramref name="time"/> and decodes their first frame
    /// in the background, so the next request a few frames ahead is instant.
    /// The cost is one decode on a background thread, paid while the user is
    /// still moving the mouse.
    /// </summary>
    private void PrimeSession(Project project, double time, EffectPreview? preview)
    {
        DropSession();
        if (_width < 2 || _height < 2) return;

        var session = new Session(
            new SequentialCompositor(_locator, _compositor),
            time, new byte[_width * _height * 4], _width, _height);
        _session = session;
        session.BeginPriming(project, ScrubFps, preview);
    }

    private void DropSession()
    {
        _session?.Dispose();
        _session = null;
    }

    /// <summary>Primed decoders sitting at a known position on the scrub frame grid.</summary>
    private sealed class Session : IDisposable
    {
        private readonly SequentialCompositor _renderer;
        private readonly CancellationTokenSource _cts = new();
        private readonly byte[] _canvas;
        private readonly int _width;
        private readonly int _height;
        private readonly double _start;

        private Task? _priming;
        private bool _primingFailed;
        private long _nextFrameIndex;

        public Session(
            SequentialCompositor renderer, double start, byte[] canvas, int width, int height)
        {
            _renderer = renderer;
            _start = start;
            _canvas = canvas;
            _width = width;
            _height = height;
        }

        /// <summary>
        /// Cheap pre-check that does not depend on the priming decode having
        /// finished: is the request inside this session's forward window at all?
        /// </summary>
        public bool MightReach(double time, double maxCatchUpSeconds) =>
            time >= _start - 0.001 && time - _start <= maxCatchUpSeconds;

        /// <summary>True when catching up to <paramref name="time"/> is cheaper than restarting.</summary>
        public bool Reaches(double time, double fps, double maxCatchUpSeconds)
        {
            var target = FrameIndexFor(time, fps);
            if (target < _nextFrameIndex) return false;           // cannot rewind a stream
            return (target - _nextFrameIndex) / fps <= maxCatchUpSeconds;
        }

        public void BeginPriming(Project project, double fps, EffectPreview? preview) =>
            _priming = Task.Run(async () =>
            {
                // Failures are recorded, never thrown: nothing awaits this task
                // when the session is dropped, and an unobserved fault helps no one.
                try
                {
                    await _renderer.RenderAsync(
                            project, _start, 0, fps, _canvas, _width, _height, _cts.Token, preview)
                        .ConfigureAwait(false);
                    _nextFrameIndex = 1;
                }
                catch
                {
                    _primingFailed = true;
                }
            }, CancellationToken.None);

        /// <summary>Waits for the priming decode; false when it could not be primed.</summary>
        public async Task<bool> WaitUntilPrimedAsync(CancellationToken cancellationToken)
        {
            if (_priming is { } priming)
            {
                await priming.WaitAsync(cancellationToken).ConfigureAwait(false);
                _priming = null;
            }
            return !_primingFailed;
        }

        /// <summary>
        /// Decodes forward to <paramref name="time"/> and returns a private copy
        /// of the canvas. Intermediate frames are decoded and dropped — that is
        /// what keeps the per-event decoders in lockstep, and each costs a few ms.
        /// </summary>
        public async Task<byte[]> AdvanceToAsync(
            Project project, double time, double fps,
            CancellationToken cancellationToken, EffectPreview? preview)
        {
            var target = Math.Max(_nextFrameIndex, FrameIndexFor(time, fps));
            for (var index = _nextFrameIndex; index <= target; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _renderer.RenderAsync(
                        project, _start + index / fps, index, fps,
                        _canvas, _width, _height, cancellationToken, preview)
                    .ConfigureAwait(false);
            }
            _nextFrameIndex = target + 1;
            return (byte[])_canvas.Clone();
        }

        private long FrameIndexFor(double time, double fps) =>
            (long)Math.Round((time - _start) * fps);

        /// <summary>
        /// Cancels priming and releases the ffmpeg processes as soon as the
        /// in-flight decode unwinds — never blocking the caller (this runs on
        /// the UI thread before every cold frame).
        /// </summary>
        public void Dispose()
        {
            _cts.Cancel();
            if (_priming is { } priming)
                _ = priming.ContinueWith(_ => Release(), TaskScheduler.Default);
            else
                Release();
        }

        private void Release()
        {
            _renderer.Dispose();
            _cts.Dispose();
        }
    }
}
