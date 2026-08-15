using System.Diagnostics;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Ffmpeg;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.MediaEngine.Playback;

/// <summary>
/// Real-time playback engine built as producer/consumer:
///
/// - A background **producer** decodes frames (streaming a single video layer
///   through one ffmpeg process when possible, composing otherwise), applies
///   effects/fades, and publishes each finished frame into a latest-wins
///   mailbox. When decoding falls behind the wall clock it drops frames, and
///   when it falls far behind it re-seeks forward — so slow sources degrade to
///   a lower frame rate instead of freezing.
/// - A fixed-rate **consumer** runs on the caller's context (the UI thread):
///   every tick it advances the playhead with the wall clock and presents the
///   newest published frame. The red playhead line therefore always moves
///   smoothly, no matter how slow decoding is.
/// </summary>
public class PlaybackEngine
{
    private const int ConsumerIntervalMs = 33;       // present/playhead tick (~30 Hz)
    private const double StaleFrameSeconds = 0.15;   // drop frames older than this
    private const double ReseekBehindSeconds = 0.6;  // decode too slow → jump forward
    private const double ImageRepublishSeconds = 0.08; // stills refresh (animated fx)

    private readonly FrameCompositor _compositor;
    private readonly FrameExtractor _extractor;
    private readonly VideoEffectPipeline _effects;
    private readonly FFmpegLocator _locator;

    public PlaybackEngine(
        FrameCompositor compositor,
        FrameExtractor extractor,
        VideoEffectPipeline effects,
        FFmpegLocator locator)
    {
        _compositor = compositor;
        _extractor = extractor;
        _effects = effects;
        _locator = locator;
    }

    /// <summary>
    /// Plays [origin, duration). <paramref name="onTime"/> and
    /// <paramref name="present"/> are invoked on the caller's context.
    /// Returns when the end is reached or the token is cancelled.
    /// </summary>
    public async Task RunAsync(
        Project project,
        double origin,
        double duration,
        int width,
        int height,
        double fps,
        Action<double> onTime,
        Action<byte[], int, int> present,
        CancellationToken token,
        Func<EffectPreview?>? previewProvider = null)
    {
        var clock = Stopwatch.StartNew();
        double Now() => origin + clock.Elapsed.TotalSeconds;

        var mailbox = new FrameMailbox();
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var producer = Task.Run(
            () => ProduceAsync(
                project, Now, duration, width, height, fps, mailbox, previewProvider, producerCts.Token),
            CancellationToken.None);

        try
        {
            // Consumer: wall-clock playhead + newest-frame presentation.
            while (!token.IsCancellationRequested)
            {
                var now = Now();
                if (now >= duration)
                {
                    onTime(duration);
                    break;
                }

                onTime(now);
                mailbox.TryPresent(present);
                await Task.Delay(ConsumerIntervalMs, token);
            }
        }
        finally
        {
            producerCts.Cancel();
            // No ConfigureAwait(false): the caller expects to resume on its own context.
            try { await producer; }
            catch { /* producer failures already degraded gracefully */ }
        }
    }

    // ---------- Producer ----------

    private async Task ProduceAsync(
        Project project, Func<double> now, double duration,
        int width, int height, double fps, FrameMailbox mailbox,
        Func<EffectPreview?>? previewProvider, CancellationToken token)
    {
        StreamingFramePipe? pipe = null;
        var pipeEventId = Guid.Empty;
        var pipeStartTime = 0.0;
        long pipeFrameIndex = 0;

        byte[]? imageBase = null;
        var imageEventId = Guid.Empty;

        void DropPipe()
        {
            pipe?.Dispose();
            pipe = null;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                var t = now();
                if (t >= duration) break;

                var layer = SafeFindLayer(project, t);

                // ---- Single video layer → stream it ----
                if (layer is { Media.Type: MediaType.Video })
                {
                    var expectedFrameTime = pipeStartTime + pipeFrameIndex / fps;
                    var needsRestart =
                        pipe is null ||
                        pipeEventId != layer.Event.Id ||
                        t - expectedFrameTime > ReseekBehindSeconds; // hopelessly behind → seek forward

                    if (needsRestart)
                    {
                        DropPipe();
                        var sourceTime = layer.Event.SourceIn +
                                         (t - layer.Event.Start) * layer.Event.PlaybackRate;
                        pipe = StreamingFramePipe.Start(
                            _locator, layer.Media.FilePath, sourceTime, layer.Event.PlaybackRate,
                            width, height, fps);
                        pipeEventId = layer.Event.Id;
                        pipeStartTime = t;
                        pipeFrameIndex = 0;
                    }

                    if (pipe is null)
                    {
                        await Task.Delay(50, token).ConfigureAwait(false);
                        continue;
                    }

                    var frameTime = pipeStartTime + pipeFrameIndex / fps;
                    var frame = await pipe.ReadFrameAsync(token).ConfigureAwait(false);
                    pipeFrameIndex++;

                    if (frame is null)
                    {
                        // Source ended before the event did (clip tail) — show black
                        // via the compose path next round; avoid a tight spin here.
                        DropPipe();
                        await Task.Delay(20, token).ConfigureAwait(false);
                        continue;
                    }

                    // Stale (decode slower than real time): skip presenting, keep reading.
                    if (frameTime < now() - StaleFrameSeconds) continue;

                    var display = ApplyLayerEffects(
                        frame, width, height, layer, frameTime, project, previewProvider?.Invoke());
                    mailbox.Publish(display, width, height);

                    // Ahead of the clock → pace down to real time.
                    var ahead = frameTime - now();
                    if (ahead > 0.005)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(ahead, 0.5)), token).ConfigureAwait(false);
                    continue;
                }

                DropPipe();

                // ---- Single still image → decode once, republish with effects ----
                if (layer is { Media.Type: MediaType.Image })
                {
                    if (imageBase is null || imageEventId != layer.Event.Id)
                    {
                        var raw = await _extractor
                            .GetFrameAsync(layer.Media.FilePath, 0, width, height, token)
                            .ConfigureAwait(false);
                        imageBase = raw?.Bgra.ToArray();
                        imageEventId = layer.Event.Id;
                    }
                    if (imageBase != null)
                    {
                        var working = (byte[])imageBase.Clone(); // effects mutate in place
                        var display = ApplyLayerEffects(
                            working, width, height, layer, t, project, previewProvider?.Invoke());
                        mailbox.Publish(display, width, height);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(ImageRepublishSeconds), token).ConfigureAwait(false);
                    continue;
                }

                // ---- Overlapping layers / empty spot → full composition ----
                try
                {
                    var composed = await _compositor
                        .ComposeAsync(project, t, width, height, token, previewProvider?.Invoke())
                        .ConfigureAwait(false);
                    mailbox.Publish(composed.Bgra, composed.Width, composed.Height);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* a missing file must not kill playback */ }
                await Task.Delay(10, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // pause/stop — expected
        }
        finally
        {
            DropPipe();
        }
    }

    /// <summary>
    /// Effects + transform + fades + opacity for a single-layer frame
    /// (producer thread). Returns the frame to present — a new buffer when a
    /// transform re-positions the layer.
    /// </summary>
    private byte[] ApplyLayerEffects(
        byte[] frame, int width, int height, FrameCompositor.VisualLayer layer, double time, Project project,
        EffectPreview? preview = null)
    {
        try
        {
            _effects.Apply(frame, width, height, layer.Event.Effects, time - layer.Event.Start);
            if (preview is { } candidate && candidate.EventId == layer.Event.Id)
                _effects.Apply(frame, width, height, new[] { candidate.Effect }, time - layer.Event.Start);
            _effects.Apply(frame, width, height, layer.Track.Effects, time);

            var positionScale = project.Settings.Width > 0 ? (double)width / project.Settings.Width : 1;
            var display = FrameCompositor.ApplyTransform(
                frame, width, height, layer.Event.Transform, positionScale);
            FrameCompositor.FlattenOnBlack(display);

            var opacity = Math.Clamp(layer.Event.Opacity, 0, 1) *
                          Math.Clamp(layer.Track.Opacity, 0, 1) *
                          FrameCompositor.EffectiveFadeFactor(layer.Track, layer.Event, time);
            FrameCompositor.ApplyOpacity(display, opacity);
            return display;
        }
        catch
        {
            // The UI may be editing the effect chain mid-frame; show it unfiltered.
            return frame;
        }
    }

    /// <summary>Model reads race with UI edits during playback — never let that throw.</summary>
    private static FrameCompositor.VisualLayer? SafeFindLayer(Project project, double time)
    {
        try { return FrameCompositor.FindSingleVisualLayer(project, time); }
        catch { return null; }
    }

    /// <summary>Latest-wins frame slot shared between producer and consumer.</summary>
    private sealed class FrameMailbox
    {
        private readonly object _gate = new();
        private byte[]? _pixels;
        private int _width;
        private int _height;
        private long _published;
        private long _presented;

        public void Publish(byte[] frame, int width, int height)
        {
            lock (_gate)
            {
                var length = width * height * 4;
                if (_pixels is null || _pixels.Length != length)
                    _pixels = new byte[length];
                Buffer.BlockCopy(frame, 0, _pixels, 0, Math.Min(length, frame.Length));
                _width = width;
                _height = height;
                _published++;
            }
        }

        public bool TryPresent(Action<byte[], int, int> present)
        {
            lock (_gate)
            {
                if (_pixels is null || _published == _presented) return false;
                present(_pixels, _width, _height);
                _presented = _published;
                return true;
            }
        }
    }
}
