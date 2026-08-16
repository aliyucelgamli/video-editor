using System.Diagnostics;
using System.Globalization;
using System.Text;
using VideoEditor.Domain;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Ffmpeg;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.MediaEngine.Diagnostics;

/// <summary>
/// Measures where preview playback time actually goes and writes a shareable
/// report: machine and FFmpeg capabilities, what the current project forces
/// the renderer to do, and timings for each stage (decode, effects, transform,
/// blend, full compose). Read the verdict at the bottom first.
/// </summary>
public class PerformanceProbe
{
    private const int PixelOpIterations = 20;

    private readonly FFmpegLocator _locator;
    private readonly FrameCompositor _compositor;
    private readonly VideoEffectPipeline _effects;

    public PerformanceProbe(FFmpegLocator locator, FrameCompositor compositor, VideoEffectPipeline effects)
    {
        _locator = locator;
        _compositor = compositor;
        _effects = effects;
    }

    public async Task<string> RunAsync(
        Project project, int width, int height, double fps,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var report = new StringBuilder();
        void Line(string text = "") => report.AppendLine(text);
        void Step(string text) => progress?.Report(text);

        Line("VIDEO EDITOR — PERFORMANCE REPORT");
        Line($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line(new string('=', 64));
        Line();

        Step("Collecting system information…");
        await AppendSystemAsync(Line, cancellationToken).ConfigureAwait(false);

        Step("Checking FFmpeg…");
        await AppendFfmpegAsync(Line, cancellationToken).ConfigureAwait(false);

        Step("Analyzing the project…");
        AppendProjectAnalysis(Line, project, width, height, fps);

        Step("Benchmarking pixel operations…");
        AppendPixelBenchmarks(Line, width, height);

        Step("Benchmarking decode and compose…");
        var timings = await AppendRenderBenchmarksAsync(
            Line, project, width, height, fps, cancellationToken).ConfigureAwait(false);

        AppendVerdict(Line, project, timings, fps);
        return report.ToString();
    }

    // ---------- Environment ----------

    private static async Task AppendSystemAsync(Action<string> line, CancellationToken cancellationToken)
    {
        line("SYSTEM");
        line($"  OS                : {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})");
        line($"  .NET              : {Environment.Version}");
        line($"  Logical CPUs      : {Environment.ProcessorCount}");
        line($"  Process memory    : {Environment.WorkingSet / (1024 * 1024)} MB");

        // PowerShell CIM keeps this dependency-free (no System.Management package).
        var cpu = await QueryCimAsync("Win32_Processor", "Name,MaxClockSpeed", cancellationToken).ConfigureAwait(false);
        var gpu = await QueryCimAsync("Win32_VideoController", "Name,DriverVersion,AdapterRAM", cancellationToken).ConfigureAwait(false);
        var memory = await QueryCimAsync("Win32_ComputerSystem", "TotalPhysicalMemory", cancellationToken).ConfigureAwait(false);

        if (cpu.Length > 0) line($"  CPU               : {cpu}");
        if (gpu.Length > 0) line($"  GPU               : {gpu}");
        if (memory.Length > 0) line($"  Installed RAM     : {memory}");
        line("");
    }

    private static async Task<string> QueryCimAsync(
        string className, string properties, CancellationToken cancellationToken)
    {
        try
        {
            var script =
                $"Get-CimInstance {className} | Select-Object -Property {properties} | " +
                "ForEach-Object { ($_.PSObject.Properties | ForEach-Object { \"$($_.Name)=$($_.Value)\" }) -join ', ' }";
            var result = await ProcessRunner.RunAsync(
                    "powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", script }, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success) return string.Empty;

            var lines = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
            return string.Join(" | ", lines);
        }
        catch
        {
            return string.Empty; // diagnostics must never fail the app
        }
    }

    private async Task AppendFfmpegAsync(Action<string> line, CancellationToken cancellationToken)
    {
        line("FFMPEG");
        if (_locator.FfmpegPath is not { } ffmpeg)
        {
            line("  NOT FOUND — preview and export are disabled.");
            line("");
            return;
        }

        line($"  Path              : {ffmpeg}");
        var version = await ProcessRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-version" }, cancellationToken)
            .ConfigureAwait(false);
        var firstLine = version.StandardOutput.Split('\n').FirstOrDefault()?.Trim();
        if (!string.IsNullOrEmpty(firstLine)) line($"  Version           : {firstLine}");

        var hwaccels = await ProcessRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-hwaccels" }, cancellationToken)
            .ConfigureAwait(false);
        var accels = hwaccels.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase))
            .ToList();
        line($"  Listed decoders   : {(accels.Count > 0 ? string.Join(", ", accels) : "none reported")}");

        var verified = await HardwareDecoders.DetectAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        line($"  Verified decoder  : {verified ?? "none — software decoding only"}");
        line("");
    }

    // ---------- What the project forces the renderer to do ----------

    private static void AppendProjectAnalysis(
        Action<string> line, Project project, int width, int height, double fps)
    {
        line("PROJECT");
        line($"  Preview canvas    : {width}x{height} @ {fps:0.#} fps  ({width * height / 1000.0:0} k pixels/frame)");
        line($"  Project           : {project.Settings.Width}x{project.Settings.Height} @ {project.Settings.FrameRate:0.#} fps");
        line($"  Duration          : {project.Duration:0.##}s");

        var visualTracks = project.Tracks.Count(t => t.Type is TrackType.Video or TrackType.Overlay);
        var audioTracks = project.Tracks.Count(t => t.Type == TrackType.Audio);
        var clips = project.Tracks.Sum(t => t.Events.Count);
        var textClips = project.Tracks.Sum(t => t.Events.Count(e => e.Text != null));
        var effects = project.Tracks.Sum(t => t.Events.Sum(e => e.Effects.Count(i => i.Enabled)));
        line($"  Tracks            : {visualTracks} visual, {audioTracks} audio");
        line($"  Clips             : {clips} ({textClips} text)");
        line($"  Enabled effects   : {effects}");

        // The decisive question: how often can playback stream a single layer,
        // and how often must it composite several at once?
        var samples = 40;
        var fast = 0;
        var composite = 0;
        var empty = 0;
        var maxLayers = 0;
        for (var i = 0; i < samples; i++)
        {
            var time = project.Duration * i / Math.Max(1, samples - 1);
            var layers = FrameCompositor.EnumerateVisibleLayers(project, time);
            maxLayers = Math.Max(maxLayers, layers.Count);

            if (layers.Count == 0) empty++;
            else if (FrameCompositor.FindSingleVisualLayer(project, time) != null) fast++;
            else composite++;
        }

        line($"  Visible layers    : up to {maxLayers} at once");
        line($"  Playback path     : {fast}/{samples} samples single-layer stream, " +
             $"{composite}/{samples} overlap compose, {empty}/{samples} empty");
        if (composite > 0)
            line("  NOTE              : overlapping layers (text over video counts) cost one " +
                 "decode + blend per layer per frame — the heaviest thing playback does.");
        line("");
    }

    // ---------- Pure CPU pixel work ----------

    private void AppendPixelBenchmarks(Action<string> line, int width, int height)
    {
        line("PIXEL OPERATIONS (per frame, average of " + PixelOpIterations + ")");

        // Decoded video frames are fully opaque, so the alpha channel is fixed
        // at 255 — random alpha would send every operation down its slow branch
        // and overstate the cost.
        var frame = new byte[width * height * 4];
        new Random(42).NextBytes(frame);
        for (var i = 3; i < frame.Length; i += 4) frame[i] = 255;
        var canvas = new byte[frame.Length];

        line($"  Frame buffer      : {frame.Length / (1024.0 * 1024.0):0.00} MB");
        line($"  FillBlack         : {Measure(() => FrameCompositor.FillBlack(canvas)):0.00} ms");
        line($"  BlendOnto (opaque): {Measure(() => FrameCompositor.BlendOnto(canvas, frame, 1.0)):0.00} ms");
        line($"  BlendOnto (50%)   : {Measure(() => FrameCompositor.BlendOnto(canvas, frame, 0.5)):0.00} ms");
        line($"  FlattenOnBlack    : {Measure(() => FrameCompositor.FlattenOnBlack(frame)):0.00} ms");
        line($"  ApplyOpacity      : {Measure(() => FrameCompositor.ApplyOpacity(frame, 0.8)):0.00} ms");

        var scaled = new Transform2D { ScaleX = 1.2, ScaleY = 1.2, PositionX = 10 };
        line($"  ApplyTransform    : {Measure(() => FrameCompositor.ApplyTransform(frame, width, height, scaled, 1)):0.00} ms " +
             "(allocates a new frame buffer)");

        var grayscale = new List<EffectInstance>();
        line($"  Effects (none)    : {Measure(() => _effects.Apply(frame, width, height, grayscale)):0.00} ms");
        line("");
    }

    private static double Measure(Action action)
    {
        action(); // warm up (JIT + caches)
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < PixelOpIterations; i++) action();
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds / PixelOpIterations;
    }

    // ---------- Decode / compose ----------

    private async Task<Timings> AppendRenderBenchmarksAsync(
        Action<string> line, Project project, int width, int height, double fps,
        CancellationToken cancellationToken)
    {
        line("RENDERING");

        var streamMs = await MeasureStreamAsync(project, width, height, fps, cancellationToken)
            .ConfigureAwait(false);
        line(streamMs > 0
            ? $"  Playback, 1 layer : {streamMs:0.0} ms/frame  ({1000 / streamMs:0.0} fps ceiling)"
            : "  Playback, 1 layer : not measured (no video clip found)");

        var overlapMs = await MeasureSequentialAsync(project, width, height, fps, cancellationToken)
            .ConfigureAwait(false);
        line(overlapMs > 0
            ? $"  Playback, overlap : {overlapMs:0.0} ms/frame  ({1000 / overlapMs:0.0} fps ceiling)"
            : "  Playback, overlap : not measured (empty timeline)");

        var composeMs = await MeasureComposeAsync(project, width, height, cancellationToken)
            .ConfigureAwait(false);
        line(composeMs > 0
            ? $"  Scrub, cold       : {composeMs:0.0} ms/frame  (landing on a new position — " +
              "one seek+decode per layer, decoded in parallel)"
            : "  Scrub, cold       : not measured (empty timeline)");

        var warmScrubMs = await MeasureWarmScrubAsync(project, width, height, cancellationToken)
            .ConfigureAwait(false);
        line(warmScrubMs > 0
            ? $"  Scrub, dragging   : {warmScrubMs:0.0} ms/frame  (continuing forward from the " +
              "last position through primed decoders)"
            : "  Scrub, dragging   : not measured (empty timeline)");

        // The open question GPU decoding has to answer: does moving the
        // seek+decode to the GPU actually beat the CPU on THIS machine?
        var gpuScrubMs = await MeasureGpuScrubAsync(project, width, height, cancellationToken)
            .ConfigureAwait(false);
        if (gpuScrubMs > 0)
            line($"  Scrub, cold on GPU: {gpuScrubMs:0.0} ms/frame  (same work with -hwaccel " +
                 $"{HardwareDecoders.Known(_locator.FfmpegPath ?? string.Empty)})");

        line("");
        return new Timings(streamMs, overlapMs, composeMs, warmScrubMs, gpuScrubMs);
    }

    private readonly record struct Timings(
        double StreamMs, double OverlapMs, double ComposeMs, double WarmScrubMs, double GpuScrubMs);

    /// <summary>
    /// Repeats the cold-scrub measurement with GPU decoding enabled on a
    /// private extractor, so the report can compare like for like.
    /// </summary>
    private async Task<double> MeasureGpuScrubAsync(
        Project project, int width, int height, CancellationToken cancellationToken)
    {
        if (project.Duration <= 0.01 || _locator.FfmpegPath is not { } ffmpeg) return 0;

        var accelerator = await HardwareDecoders.DetectAsync(ffmpeg, cancellationToken).ConfigureAwait(false);
        if (accelerator is null) return 0;

        var extractor = new FrameExtractor(_locator) { HardwareAccelerator = accelerator };
        var compositor = new FrameCompositor(extractor, _effects);
        foreach (var (style, raster) in _compositor.TextRasters.Snapshot())
            compositor.TextRasters.StoreRaw(style, raster);

        var stopwatch = Stopwatch.StartNew();
        const int frames = 5;
        for (var i = 0; i < frames; i++)
        {
            var time = project.Duration * (i + 1) / (frames + 1);
            await compositor.ComposeAsync(project, time, width, height, cancellationToken)
                .ConfigureAwait(false);
        }
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds / frames;
    }

    /// <summary>
    /// What dragging the playhead forward costs once <see cref="ScrubRenderer"/>
    /// has primed decoders — the first render is excluded, exactly as the UI
    /// pays it once when the drag starts.
    /// </summary>
    private async Task<double> MeasureWarmScrubAsync(
        Project project, int width, int height, CancellationToken cancellationToken)
    {
        if (project.Duration <= 0.05) return 0;

        using var scrub = new ScrubRenderer(_locator, _compositor);
        var start = project.Duration * 0.25;
        await scrub.RenderAsync(project, start, width, height, cancellationToken).ConfigureAwait(false);

        const int steps = 8;
        const double stepSeconds = 0.05; // a fast drag lands roughly this far apart
        var stopwatch = Stopwatch.StartNew();
        for (var i = 1; i <= steps; i++)
            await scrub.RenderAsync(project, start + i * stepSeconds, width, height, cancellationToken)
                .ConfigureAwait(false);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds / steps;
    }

    /// <summary>The fast path: one long-lived decoder, frames read back to back.</summary>
    private async Task<double> MeasureStreamAsync(
        Project project, int width, int height, double fps, CancellationToken cancellationToken)
    {
        var layer = FindFirstVideoLayer(project);
        if (layer is null) return 0;

        using var pipe = StreamingFramePipe.Start(
            _locator, layer.Value.Media.FilePath, layer.Value.Event.SourceIn,
            layer.Value.Event.PlaybackRate, width, height, fps);
        if (pipe is null) return 0;

        var frames = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 24; i++)
        {
            if (await pipe.ReadFrameAsync(cancellationToken).ConfigureAwait(false) is null) break;
            frames++;
        }
        stopwatch.Stop();
        return frames == 0 ? 0 : stopwatch.Elapsed.TotalMilliseconds / frames;
    }

    /// <summary>
    /// What playback does when layers overlap: consecutive frames through the
    /// sequential renderer, one decoder per event kept alive across frames.
    /// </summary>
    private async Task<double> MeasureSequentialAsync(
        Project project, int width, int height, double fps, CancellationToken cancellationToken)
    {
        if (project.Duration <= 0.01) return 0;

        using var sequential = new SequentialCompositor(_locator, _compositor);
        var canvas = new byte[width * height * 4];
        var start = project.Duration * 0.25;

        // The first frame pays for starting the decoders — exclude it, the same
        // way playback pays that cost once per run rather than once per frame.
        await sequential.RenderAsync(project, start, 0, fps, canvas, width, height, cancellationToken)
            .ConfigureAwait(false);

        const int frames = 12;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 1; i <= frames; i++)
            await sequential.RenderAsync(project, start + i / fps, i, fps, canvas, width, height, cancellationToken)
                .ConfigureAwait(false);
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds / frames;
    }

    /// <summary>The per-frame path scrubbing uses: seek + decode for every layer.</summary>
    private async Task<double> MeasureComposeAsync(
        Project project, int width, int height, CancellationToken cancellationToken)
    {
        if (project.Duration <= 0.01) return 0;

        var stopwatch = Stopwatch.StartNew();
        const int frames = 5;
        for (var i = 0; i < frames; i++)
        {
            var time = project.Duration * (i + 1) / (frames + 1);
            await _compositor.ComposeAsync(project, time, width, height, cancellationToken)
                .ConfigureAwait(false);
        }
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds / frames;
    }

    private static (Track Track, TimelineEvent Event, MediaItem Media)? FindFirstVideoLayer(Project project)
    {
        foreach (var track in project.Tracks.Where(t => t.Type is TrackType.Video or TrackType.Overlay))
            foreach (var evt in track.Events)
                if (project.Media.FindById(evt.MediaId) is { Type: MediaType.Video } media)
                    return (track, evt, media);
        return null;
    }

    // ---------- Verdict ----------

    private static void AppendVerdict(
        Action<string> line, Project project, Timings timings, double fps)
    {
        var budget = 1000.0 / Math.Max(1, fps);
        line("VERDICT");
        line($"  Frame budget at {fps:0.#} fps: {budget:0.0} ms");

        var usesOverlap = false;
        for (var i = 0; i < 20 && !usesOverlap; i++)
        {
            var time = project.Duration * i / 19.0;
            if (FrameCompositor.EnumerateVisibleLayers(project, time).Count > 0 &&
                FrameCompositor.FindSingleVisualLayer(project, time) is null)
                usesOverlap = true;
        }

        // The path that actually runs during playback here.
        var playbackMs = usesOverlap ? timings.OverlapMs : timings.StreamMs;

        if (playbackMs > budget)
            line($"  * Playback costs {playbackMs:0} ms/frame — about {1000 / Math.Max(1, playbackMs):0.#} fps, " +
                 $"below the {fps:0.#} fps target. " +
                 (usesOverlap
                     ? "Overlapping layers are the cost; fewer simultaneous layers or a lower preview quality both help."
                     : "Decoding alone is heavy for this machine at the current preview size — lower the preview quality or use proxy media."));
        else
            line($"  * Playback fits the budget at {playbackMs:0} ms/frame. If it still stutters, the cost is " +
                 "in presentation (bitmap scaling, UI thread) rather than rendering.");

        if (timings.ComposeMs > 0)
        {
            line($"  * Landing on a new frame costs {timings.ComposeMs:0} ms — that is ffmpeg seeking " +
                 "and decoding from the nearest keyframe, once per layer, and it cannot be avoided.");
            if (timings.WarmScrubMs > 0)
                line($"  * Continuing the drag costs {timings.WarmScrubMs:0} ms/frame " +
                     $"({timings.ComposeMs / Math.Max(1, timings.WarmScrubMs):0.#}x cheaper) because the " +
                     "decoders primed at the previous position are reused.");
        }

        line("");
        line("Share this file with Claude to decide what to optimize next.");
    }
}
