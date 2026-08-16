using System.Runtime.InteropServices;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Effects;

namespace VideoEditor.MediaEngine.Frames;

/// <summary>
/// Composes the frame visible at a given timeline position: visual tracks are
/// painted bottom-up (the topmost track in the UI renders on top), each event
/// runs through its effect chain, then opacity/fades blend it onto the canvas.
/// Preview and export share this class so they can never diverge.
/// </summary>
public class FrameCompositor
{
    private readonly FrameExtractor _extractor;
    private readonly VideoEffectPipeline _effects;

    public FrameCompositor(FrameExtractor extractor, VideoEffectPipeline effects)
    {
        _extractor = extractor;
        _effects = effects;
    }

    /// <summary>Shared frame source (used by the export renderer's fallback path).</summary>
    public FrameExtractor Extractor => _extractor;

    /// <summary>Rasterized text layers, filled by the UI's text rasterizer.</summary>
    public TextRasterCache TextRasters { get; } = new();

    /// <summary>
    /// Renders the timeline at <paramref name="time"/> into a BGRA canvas.
    /// Returns a black frame when nothing is visible.
    /// </summary>
    public async Task<RawFrame> ComposeAsync(
        Project project, double time, int width, int height,
        CancellationToken cancellationToken = default, EffectPreview? preview = null)
    {
        width -= width % 2;
        height -= height % 2;
        var canvas = new byte[width * height * 4];
        FillBlack(canvas);

        var layers = EnumerateVisibleLayers(project, time);

        // Every source is decoded in its own ffmpeg process, so the layers are
        // fetched CONCURRENTLY: the wait is the slowest single decode instead
        // of the sum of all of them. Blending stays strictly back to front.
        var sources = new Task<byte[]?>[layers.Count];
        for (var i = 0; i < layers.Count; i++)
            sources[i] = AcquireLayerAsync(project, layers[i], time, width, height, cancellationToken);

        var decoded = await Task.WhenAll(sources).ConfigureAwait(false);

        for (var i = 0; i < layers.Count; i++)
        {
            if (decoded[i] is not { } layer) continue;
            var (track, evt) = layers[i];
            BlendLayerOnto(canvas, layer, width, height, track, evt, time, project, preview);
        }

        return new RawFrame(canvas, width, height);
    }

    /// <summary>
    /// Source pixels for one layer, ready to be mutated by the caller (effects
    /// run in place). Null when the layer contributes nothing at this time.
    /// </summary>
    private async Task<byte[]?> AcquireLayerAsync(
        Project project, LayerEntry entry, double time, int width, int height,
        CancellationToken cancellationToken)
    {
        var evt = entry.Event;
        if (evt.Text is { } textStyle)
        {
            // Rasterized by the UI; a private copy because effects mutate.
            var raster = TextRasters.TryGetShared(textStyle, width, height);
            return raster is null ? null : (byte[])raster.Bgra.Clone();
        }

        var media = project.Media.FindById(evt.MediaId);
        if (media is null || media.Type == MediaType.Audio) return null;

        var sourceTime = media.Type == MediaType.Image
            ? 0
            : evt.SourceIn + (time - evt.Start) * evt.PlaybackRate;
        var frame = await _extractor
            .GetFrameAsync(media.FilePath, sourceTime, width, height, cancellationToken)
            .ConfigureAwait(false);
        return frame?.Bgra;
    }

    /// <summary>
    /// Applies a layer's effect chains, transform, opacity and fades, then
    /// blends it onto the canvas. This is the single home of the per-layer
    /// composition math — the sequential export renderer calls it too, so
    /// export pixels can never diverge from preview.
    /// </summary>
    public void BlendLayerOnto(
        byte[] canvas, byte[] layerBgra, int width, int height,
        Track track, TimelineEvent evt, double time, Project project,
        EffectPreview? preview = null)
    {
        // Event effects animate on clip-local time, track effects on timeline time.
        _effects.Apply(layerBgra, width, height, evt.Effects, time - evt.Start);
        if (preview is { } candidate && candidate.EventId == evt.Id)
            _effects.Apply(layerBgra, width, height, new[] { candidate.Effect }, time - evt.Start);
        _effects.Apply(layerBgra, width, height, track.Effects, time);

        var positionScale = project.Settings.Width > 0 ? (double)width / project.Settings.Width : 1;
        var pixels = ApplyTransform(layerBgra, width, height, evt.Transform, positionScale);

        var opacity = Math.Clamp(evt.Opacity, 0, 1) *
                      Math.Clamp(track.Opacity, 0, 1) *
                      EffectiveFadeFactor(track, evt, time);
        BlendOnto(canvas, pixels, opacity);
    }

    /// <summary>One visual layer visible at a point in time (playback fast path).</summary>
    public record VisualLayer(Track Track, TimelineEvent Event, MediaItem Media);

    /// <summary>
    /// Returns the single visual layer visible at <paramref name="time"/>, or
    /// null when zero or multiple layers overlap. Playback streams this single
    /// layer directly (fast); overlaps fall back to full composition.
    /// </summary>
    public static VisualLayer? FindSingleVisualLayer(Project project, double time)
    {
        VisualLayer? found = null;
        foreach (var (track, evt) in EnumerateVisibleLayers(project, time))
        {
            if (evt.Text != null) return null; // text always composites
            var media = project.Media.FindById(evt.MediaId);
            if (media is null || media.Type == MediaType.Audio) continue;
            if (found != null) return null; // more than one layer → composite path
            found = new VisualLayer(track, evt, media);
        }
        return found;
    }

    /// <summary>True when a transform would not change the frame (fast skip).</summary>
    public static bool IsIdentityTransform(Transform2D t) =>
        Math.Abs(t.ScaleX - 1) < 0.001 && Math.Abs(t.ScaleY - 1) < 0.001 &&
        Math.Abs(t.PositionX) < 0.01 && Math.Abs(t.PositionY) < 0.01;

    /// <summary>
    /// Applies scale + position to a layer frame (nearest-neighbor, alpha-safe).
    /// Positions are stored in project pixels; <paramref name="positionScale"/>
    /// converts them to canvas pixels (canvasWidth / projectWidth), so preview
    /// and full-resolution export place the layer identically. Uncovered areas
    /// become transparent, letting lower layers show through.
    /// Returns the same array when the transform is identity.
    /// </summary>
    public static byte[] ApplyTransform(
        byte[] bgra, int width, int height, Transform2D transform, double positionScale)
    {
        if (IsIdentityTransform(transform)) return bgra;

        var scaleX = Math.Clamp(transform.ScaleX, 0.01, 20);
        var scaleY = Math.Clamp(transform.ScaleY, 0.01, 20);
        var offsetX = transform.PositionX * positionScale;
        var offsetY = transform.PositionY * positionScale;
        var centerX = (width - 1) / 2.0;
        var centerY = (height - 1) / 2.0;

        // The source column only depends on the destination column, so the
        // inverse X mapping is computed once instead of per pixel (-1 = outside).
        var columnMap = new int[width];
        for (var x = 0; x < width; x++)
        {
            var sourceX = (int)Math.Round(centerX + (x - centerX - offsetX) / scaleX);
            columnMap[x] = sourceX >= 0 && sourceX < width ? sourceX : -1;
        }

        var result = new byte[bgra.Length]; // all-transparent
        var source = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
        var destination = MemoryMarshal.Cast<byte, uint>(result.AsSpan());

        for (var y = 0; y < height; y++)
        {
            // Inverse mapping: destination pixel → source pixel.
            var sourceY = (int)Math.Round(centerY + (y - centerY - offsetY) / scaleY);
            if (sourceY < 0 || sourceY >= height) continue;

            var sourceRow = sourceY * width;
            var destinationRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var sourceX = columnMap[x];
                if (sourceX < 0) continue;
                destination[destinationRow + x] = source[sourceRow + sourceX]; // whole pixel at once
            }
        }
        return result;
    }

    /// <summary>Flattens transparency onto black (single-layer playback fast path).</summary>
    public static void FlattenOnBlack(byte[] bgra)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
        for (var p = 0; p < pixels.Length; p++)
        {
            var pixel = pixels[p];
            var alpha = (int)(pixel >> 24);
            if (alpha == 255) continue;      // already opaque — the common case
            if (alpha == 0) { pixels[p] = OpaqueBlack; continue; }

            var blue = (int)(pixel & 0xFF) * alpha / 255;
            var green = (int)((pixel >> 8) & 0xFF) * alpha / 255;
            var red = (int)((pixel >> 16) & 0xFF) * alpha / 255;
            pixels[p] = OpaqueBlack | (uint)(red << 16) | (uint)(green << 8) | (uint)blue;
        }
    }

    /// <summary>
    /// Multiplies a frame toward black (event/track opacity + fades). Fixed
    /// point: one integer multiply and shift per channel instead of a
    /// double multiply, which matters at ~1M channels per frame.
    /// </summary>
    public static void ApplyOpacity(byte[] bgra, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity >= 0.999) return;

        if (opacity <= 0.0005)
        {
            // Fully faded: black out colour, keep alpha.
            for (var i = 0; i < bgra.Length; i += 4)
            {
                bgra[i] = 0;
                bgra[i + 1] = 0;
                bgra[i + 2] = 0;
            }
            return;
        }

        var scale = (int)(opacity * 65536);
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(bgra[i] * scale >> 16);
            bgra[i + 1] = (byte)(bgra[i + 1] * scale >> 16);
            bgra[i + 2] = (byte)(bgra[i + 2] * scale >> 16);
        }
    }

    /// <summary>Fade-in/out progress at the given timeline position (0..1).</summary>
    public static double FadeFactor(TimelineEvent evt, double time) => FadeFactor(evt, time, 0, 0);

    /// <summary>
    /// Fade factor combining the event's own eased fades with implicit
    /// crossfade durations (same-track overlaps). The longer of the explicit
    /// and implicit duration wins per side.
    /// </summary>
    public static double FadeFactor(
        TimelineEvent evt, double time, double implicitFadeIn, double implicitFadeOut)
    {
        var factor = 1.0;
        var fadeIn = Math.Max(evt.FadeInDuration, implicitFadeIn);
        if (fadeIn > 0 && time < evt.Start + fadeIn)
            factor = Math.Min(factor, Easing.Evaluate(evt.FadeInEasing, (time - evt.Start) / fadeIn));

        var fadeOut = Math.Max(evt.FadeOutDuration, implicitFadeOut);
        if (fadeOut > 0 && time > evt.End - fadeOut)
            factor = Math.Min(factor, Easing.Evaluate(evt.FadeOutEasing, (evt.End - time) / fadeOut));

        return Math.Clamp(factor, 0, 1);
    }

    /// <summary>Explicit fades plus automatic crossfades from same-track overlaps.</summary>
    public static double EffectiveFadeFactor(Track track, TimelineEvent evt, double time)
    {
        var (fadeIn, fadeOut) = Crossfade.ImplicitFades(track, evt);
        return FadeFactor(evt, time, fadeIn, fadeOut);
    }

    /// <summary>One visual clip to paint, already in back-to-front order.</summary>
    public readonly record struct LayerEntry(Track Track, TimelineEvent Event);

    /// <summary>
    /// Every visual clip visible at <paramref name="time"/>, ordered back to
    /// front: by effective layer (track layer + clip layer), then by track
    /// position — the top lane in the UI sits at the bottom of the stack, so
    /// the default V1 / A1 / T1 layout puts titles above the footage.
    /// </summary>
    public static List<LayerEntry> EnumerateVisibleLayers(Project project, double time)
    {
        var entries = new List<(LayerEntry Entry, int Layer, int TrackIndex, double Start)>();
        for (var i = 0; i < project.Tracks.Count; i++)
        {
            var track = project.Tracks[i];
            if (track.Type is not (TrackType.Video or TrackType.Overlay)) continue;
            if (track.Muted) continue;

            foreach (var evt in track.Events)
            {
                if (!evt.Contains(time)) continue;
                entries.Add((new LayerEntry(track, evt), Layers.Effective(track, evt), i, evt.Start));
            }
        }

        return entries
            .OrderBy(e => e.Layer)
            .ThenBy(e => e.TrackIndex)
            .ThenBy(e => e.Start)
            .Select(e => e.Entry)
            .ToList();
    }

    /// <summary>Visual tracks in lane order (top lane first).</summary>
    public static IEnumerable<Track> EnumerateVisualTracks(Project project) =>
        project.Tracks.Where(t => t.Type is TrackType.Video or TrackType.Overlay);

    /// <summary>
    /// Resets a canvas to opaque black. One 32-bit store per pixel — four times
    /// fewer writes than clearing bytes and then patching the alpha channel.
    /// </summary>
    public static void FillBlack(byte[] canvas) =>
        MemoryMarshal.Cast<byte, uint>(canvas.AsSpan()).Fill(OpaqueBlack);

    /// <summary>BGRA little-endian: alpha 255, colour 0.</summary>
    private const uint OpaqueBlack = 0xFF000000u;

    /// <summary>
    /// Alpha-blends a layer onto the canvas at the given opacity. Works a
    /// pixel (32 bits) at a time in fixed point: the fully-opaque case becomes
    /// a single store, and the blended case avoids floating point entirely.
    /// </summary>
    public static void BlendOnto(byte[] canvas, byte[] layer, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity <= 0) return;

        var canvasPixels = MemoryMarshal.Cast<byte, uint>(canvas.AsSpan());
        var layerPixels = MemoryMarshal.Cast<byte, uint>(layer.AsSpan());
        var count = Math.Min(canvasPixels.Length, layerPixels.Length);
        var layerOpacity = (int)Math.Round(opacity * 255);

        for (var p = 0; p < count; p++)
        {
            var source = layerPixels[p];
            int alpha = (int)(source >> 24);
            if (alpha == 0) continue; // fully transparent source pixel

            // Combine the layer's own alpha (transparent PNGs, text) with the
            // event/track opacity into a single 0..255 weight.
            if (layerOpacity < 255) alpha = alpha * layerOpacity / 255;

            if (alpha >= 255)
            {
                canvasPixels[p] = source | OpaqueBlack; // opaque → straight copy
                continue;
            }
            if (alpha == 0) continue;

            var destination = canvasPixels[p];
            var blue = (int)(destination & 0xFF);
            var green = (int)((destination >> 8) & 0xFF);
            var red = (int)((destination >> 16) & 0xFF);

            blue += ((int)(source & 0xFF) - blue) * alpha / 255;
            green += ((int)((source >> 8) & 0xFF) - green) * alpha / 255;
            red += ((int)((source >> 16) & 0xFF) - red) * alpha / 255;

            canvasPixels[p] = OpaqueBlack | (uint)(red << 16) | (uint)(green << 8) | (uint)blue;
        }
    }
}
