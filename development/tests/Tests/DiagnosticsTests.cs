using VideoEditor.Application.Effects;
using VideoEditor.Application.Settings;
using VideoEditor.Domain;
using VideoEditor.MediaEngine;
using VideoEditor.MediaEngine.Diagnostics;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Ffmpeg;
using VideoEditor.MediaEngine.Frames;

namespace VideoEditor.Tests;

/// <summary>
/// The performance probe is a diagnostic tool, so its contract is simple: it
/// must always produce a report and never throw — including when ffmpeg is
/// missing or the timeline is empty, which is exactly when a user reaches for it.
/// </summary>
public static class DiagnosticsTests
{
    public static void Register()
    {
        TestRunner.Add("Preview quality: presets map widths and round-trip settings", () =>
        {
            Assert.Equal(3, PreviewQuality.All.Count, "three presets");
            Assert.Equal(640, PreviewQuality.Normal.Width, "normal width");
            Assert.True(
                PreviewQuality.Draft.Width < PreviewQuality.Normal.Width &&
                PreviewQuality.Normal.Width < PreviewQuality.High.Width,
                "presets are ordered cheapest first");

            Assert.Equal(PreviewQuality.High, PreviewQuality.ForWidth(960), "exact match");
            Assert.Equal(PreviewQuality.Draft, PreviewQuality.ForWidth(500), "nearest match rounds down");
            Assert.Equal(PreviewQuality.High, PreviewQuality.ForWidth(4000), "out of range clamps to nearest");
            Assert.Equal(640, new AppSettings().PreviewWidth, "default is the balanced preset");
        });

        TestRunner.Add("Probe: reports on an empty project without throwing", () =>
        {
            var report = RunProbe(new Project());
            Assert.True(report.Contains("SYSTEM"), "system section present");
            Assert.True(report.Contains("PROJECT"), "project section present");
            Assert.True(report.Contains("PIXEL OPERATIONS"), "pixel section present");
            Assert.True(report.Contains("VERDICT"), "verdict present");
        });

        TestRunner.Add("Probe: names the compose path when layers overlap", () =>
        {
            var locator = new FFmpegLocator(Environment.CurrentDirectory);
            if (!locator.IsAvailable)
            {
                Console.WriteLine("        (skipped — ffmpeg not found on this machine)");
                return;
            }

            var report = RunProbe(BuildOverlappingProject());
            Assert.True(report.Contains("overlap compose"), "playback path breakdown present");
            Assert.True(report.Contains("Playback, overlap"), "the overlap path is timed separately");
            Assert.True(report.Contains("NOTE"), "the overlap warning fires when layers stack");
        });

        TestRunner.Add("Hardware decoders: parses an -hwaccels listing", () =>
        {
            var names = HardwareDecoders.ParseAcceleratorNames(
                "Hardware acceleration methods:\ncuda\nvaapi\ndxva2\nqsv\nd3d11va\n\n");
            Assert.True(names.Contains("cuda") && names.Contains("d3d11va"), "accelerators parsed");
            Assert.True(!names.Contains("Hardware acceleration methods:"), "the header is not a method");
            Assert.Equal(5, names.Count, "exactly the listed methods");
        });

        TestRunner.Add("Scrub renderer: reuses primed decoders when moving forward", () =>
        {
            var locator = new FFmpegLocator(Environment.CurrentDirectory);
            if (!locator.IsAvailable)
            {
                Console.WriteLine("        (skipped — ffmpeg not found on this machine)");
                return;
            }

            var workDir = Path.Combine(Path.GetTempPath(), $"ve_scrub_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            try { ScrubForwardAsync(locator, workDir).GetAwaiter().GetResult(); }
            finally { Directory.Delete(workDir, recursive: true); }
        });
    }

    private static async Task ScrubForwardAsync(FFmpegLocator locator, string workDir)
    {
        var clip = Path.Combine(workDir, "clip.mp4");
        var generated = await ProcessRunner.RunAsync(locator.FfmpegPath!, new[]
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=3",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", clip
        });
        Assert.True(generated.Success, "test clip generated");

        var project = new Project();
        var media = new MediaItem
        {
            Name = "clip.mp4", FilePath = clip, Type = MediaType.Video, DurationSeconds = 3
        };
        project.Media.Items.Add(media);
        var track = new Track { Name = "V1", Type = TrackType.Video };
        track.Events.Add(new TimelineEvent { MediaId = media.Id, Start = 0, Duration = 3, SourceOut = 3 });
        project.Tracks.Add(track);

        var effects = new VideoEffectPipeline(new EffectCatalog());
        var compositor = new FrameCompositor(new FrameExtractor(locator), effects);
        using var scrub = new ScrubRenderer(locator, compositor);

        // Landing somewhere is cold by definition…
        var first = await scrub.RenderAsync(project, 1.0, 320, 180, CancellationToken.None);
        Assert.Equal(320 * 180 * 4, first.Bgra.Length, "cold frame size");
        Assert.True(!scrub.LastFrameWasWarm, "the first position cannot be warm");

        // …continuing the drag forward must come from the primed decoders.
        var second = await scrub.RenderAsync(project, 1.1, 320, 180, CancellationToken.None);
        Assert.Equal(320 * 180 * 4, second.Bgra.Length, "warm frame size");
        Assert.True(scrub.LastFrameWasWarm, "moving forward reuses the primed decoders");

        // Jumping far away is out of the catch-up window → cold again, still correct.
        var far = await scrub.RenderAsync(project, 2.8, 320, 180, CancellationToken.None);
        Assert.Equal(320 * 180 * 4, far.Bgra.Length, "far frame size");
        Assert.True(!scrub.LastFrameWasWarm, "a distant jump restarts the decoders");

        // Invalidate (a model edit) must force the next frame cold.
        scrub.Invalidate();
        await scrub.RenderAsync(project, 2.85, 320, 180, CancellationToken.None);
        Assert.True(!scrub.LastFrameWasWarm, "an edit drops the primed decoders");
    }

    private static string RunProbe(Project project)
    {
        var locator = new FFmpegLocator(Environment.CurrentDirectory);
        var effects = new VideoEffectPipeline(new EffectCatalog());
        var compositor = new FrameCompositor(new FrameExtractor(locator), effects);
        return new PerformanceProbe(locator, compositor, effects)
            .RunAsync(project, 160, 90, 24, null, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    /// <summary>A title over a still: the shape that forces the slow path.</summary>
    private static Project BuildOverlappingProject()
    {
        var project = new Project();
        var media = new MediaItem
        {
            Name = "still.png", FilePath = "missing.png", Type = MediaType.Image, DurationSeconds = 5
        };
        project.Media.Items.Add(media);

        var video = new Track { Name = "V1", Type = TrackType.Video };
        video.Events.Add(new TimelineEvent { MediaId = media.Id, Start = 0, Duration = 5, SourceOut = 5 });
        project.Tracks.Add(video);

        var overlay = new Track { Name = "T1", Type = TrackType.Overlay };
        overlay.Events.Add(new TimelineEvent
        {
            Start = 0, Duration = 5, SourceOut = 5,
            Text = new TextStyle { Content = "Title" }
        });
        project.Tracks.Add(overlay);
        return project;
    }
}
