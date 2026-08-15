using VideoEditor.Domain;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>
/// Renders consecutive timeline frames (the export path). Per-layer pixel math
/// is delegated to <see cref="FrameCompositor.BlendLayerOnto"/>, so the output
/// matches the preview; what changes is how source frames are decoded: every
/// video event keeps ONE streaming ffmpeg decoder alive and reads the next
/// frame from it, instead of spawning a seek+decode process per frame
/// (100+ ms each). Still images are decoded once and reused. This is what
/// turns a minutes-long export into seconds.
/// </summary>
public sealed class SequentialCompositor : IDisposable
{
    private readonly FFmpegLocator _locator;
    private readonly FrameCompositor _compositor;
    private readonly Dictionary<Guid, EventStream> _streams = new();
    private readonly Dictionary<Guid, (TimelineEvent Event, byte[] Pixels)> _stills = new();
    private byte[]? _layerBuffer;

    public SequentialCompositor(FFmpegLocator locator, FrameCompositor compositor)
    {
        _locator = locator;
        _compositor = compositor;
    }

    /// <summary>
    /// Composes the frame at <paramref name="time"/> into <paramref name="canvas"/>.
    /// Frames must be requested with consecutive <paramref name="frameIndex"/>
    /// values (0, 1, 2…) so the per-event decoders stay in lockstep.
    /// </summary>
    public async Task RenderAsync(
        Project project, double time, long frameIndex, double fps,
        byte[] canvas, int width, int height, CancellationToken cancellationToken)
    {
        FrameCompositor.FillBlack(canvas);

        foreach (var track in FrameCompositor.EnumerateVisualTracksBottomUp(project))
        {
            if (track.Muted) continue;

            foreach (var evt in track.Events)
            {
                if (!evt.Contains(time)) continue;
                cancellationToken.ThrowIfCancellationRequested();

                byte[]? layer;
                if (evt.Text is { } textStyle)
                {
                    layer = AcquireTextLayer(textStyle, width, height);
                }
                else
                {
                    var media = project.Media.FindById(evt.MediaId);
                    if (media is null || media.Type == MediaType.Audio) continue;
                    layer = await AcquireLayerAsync(
                            evt, media, time, frameIndex, fps, width, height, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (layer is null) continue;

                _compositor.BlendLayerOnto(canvas, layer, width, height, track, evt, time, project);
            }
        }

        ReleaseFinishedSources(time);
    }

    /// <summary>
    /// Returns the source pixels for one layer at the given frame. The buffer
    /// is only valid until the next call — callers must consume it immediately
    /// (which BlendLayerOnto does).
    /// </summary>
    private async Task<byte[]?> AcquireLayerAsync(
        TimelineEvent evt, MediaItem media, double time, long frameIndex, double fps,
        int width, int height, CancellationToken cancellationToken)
    {
        if (media.Type == MediaType.Image)
            return await AcquireStillAsync(evt, media, width, height, cancellationToken).ConfigureAwait(false);
        return await AcquireVideoFrameAsync(evt, media, time, frameIndex, fps, width, height, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Text layers come pre-rasterized; copy into the scratch buffer.</summary>
    private byte[]? AcquireTextLayer(TextStyle style, int width, int height)
    {
        var raster = _compositor.TextRasters.TryGetShared(style, width, height);
        if (raster is null) return null;

        _layerBuffer ??= new byte[width * height * 4];
        Buffer.BlockCopy(raster.Bgra, 0, _layerBuffer, 0, raster.Bgra.Length);
        return _layerBuffer;
    }

    private async Task<byte[]?> AcquireVideoFrameAsync(
        TimelineEvent evt, MediaItem media, double time, long frameIndex, double fps,
        int width, int height, CancellationToken cancellationToken)
    {

        var stream = EnsureStream(evt, media, time, frameIndex, fps, width, height);
        if (stream.Broken)
        {
            // Decoder could not start → per-frame extraction, same as preview.
            var sourceTime = evt.SourceIn + (time - evt.Start) * evt.PlaybackRate;
            var frame = await _compositor.Extractor
                .GetFrameAsync(media.FilePath, sourceTime, width, height, cancellationToken)
                .ConfigureAwait(false);
            return frame?.Bgra;
        }
        if (stream.Ended) return null;

        var pixels = await stream.Pipe!.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        stream.NextFrameIndex++;
        if (pixels is null) stream.Ended = true; // source shorter than the event tail
        return pixels;
    }

    /// <summary>Still images: decode once per event, hand out a reusable working copy.</summary>
    private async Task<byte[]?> AcquireStillAsync(
        TimelineEvent evt, MediaItem media, int width, int height, CancellationToken cancellationToken)
    {
        if (!_stills.TryGetValue(evt.Id, out var still))
        {
            var frame = await _compositor.Extractor
                .GetFrameAsync(media.FilePath, 0, width, height, cancellationToken)
                .ConfigureAwait(false);
            if (frame is null) return null;
            still = (evt, frame.Bgra);
            _stills[evt.Id] = still;
        }

        // Effects mutate in place, so the pristine baseline is copied into a
        // scratch buffer that is reused frame after frame (zero allocations).
        _layerBuffer ??= new byte[width * height * 4];
        Buffer.BlockCopy(still.Pixels, 0, _layerBuffer, 0, still.Pixels.Length);
        return _layerBuffer;
    }

    private EventStream EnsureStream(
        TimelineEvent evt, MediaItem media, double time, long frameIndex, double fps, int width, int height)
    {
        if (_streams.TryGetValue(evt.Id, out var existing))
        {
            if (existing.Broken || existing.Ended || existing.NextFrameIndex == frameIndex)
                return existing;
            existing.Dispose(); // out of step (should not happen in sequential use) → restart
            _streams.Remove(evt.Id);
        }

        var sourceTime = evt.SourceIn + (time - evt.Start) * evt.PlaybackRate;
        var pipe = StreamingFramePipe.Start(
            _locator, media.FilePath, sourceTime, evt.PlaybackRate, width, height, fps);
        var stream = new EventStream(evt) { Pipe = pipe, NextFrameIndex = frameIndex, Broken = pipe is null };
        _streams[evt.Id] = stream;
        return stream;
    }

    /// <summary>Closes decoders and drops cached stills for events the playhead has passed.</summary>
    private void ReleaseFinishedSources(double time)
    {
        foreach (var (id, stream) in _streams.Where(s => time >= s.Value.Event.End).ToList())
        {
            stream.Dispose();
            _streams.Remove(id);
        }
        foreach (var (id, _) in _stills.Where(s => time >= s.Value.Event.End).ToList())
            _stills.Remove(id);
    }

    public void Dispose()
    {
        foreach (var stream in _streams.Values) stream.Dispose();
        _streams.Clear();
        _stills.Clear();
    }

    private sealed class EventStream : IDisposable
    {
        public EventStream(TimelineEvent evt) => Event = evt;

        public TimelineEvent Event { get; }
        public StreamingFramePipe? Pipe { get; init; }
        public long NextFrameIndex { get; set; }
        public bool Ended { get; set; }
        public bool Broken { get; init; }

        public void Dispose() => Pipe?.Dispose();
    }
}
