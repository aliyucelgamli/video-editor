using VideoEditor.Application.Effects;
using VideoEditor.Domain;
using VideoEditor.MediaEngine;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Export;
using VideoEditor.MediaEngine.Ffmpeg;
using VideoEditor.MediaEngine.Frames;
using VideoEditor.MediaEngine.Thumbnails;
using VideoEditor.MediaEngine.Waveform;

namespace VideoEditor.Tests;

/// <summary>
/// End-to-end media tests. They generate tiny clips with ffmpeg itself and are
/// skipped (reported as PASS with a note) when ffmpeg is not installed.
/// </summary>
public static class FfmpegIntegrationTests
{
    public static void Register()
    {
        var locator = new FFmpegLocator(Environment.CurrentDirectory);

        TestRunner.Add("FFmpeg integration: probe → thumbnail → waveform → frame → export", () =>
        {
            if (!locator.IsAvailable)
            {
                Console.WriteLine("        (skipped — ffmpeg not found on this machine)");
                return;
            }

            var workDir = Path.Combine(Path.GetTempPath(), $"ve_it_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            try { RunAsync(locator, workDir).GetAwaiter().GetResult(); }
            finally { Directory.Delete(workDir, recursive: true); }
        });
    }

    private static async Task RunAsync(FFmpegLocator locator, string workDir)
    {
        // 1) Generate a 2 s test clip with tone audio.
        var clip = Path.Combine(workDir, "clip.mp4");
        var generate = await ProcessRunner.RunAsync(locator.FfmpegPath!, new[]
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=2",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest", clip
        });
        Assert.True(generate.Success, "test clip generated: " + generate.StandardError);

        // 2) Probe.
        var probe = new MediaProbe(locator);
        var info = await probe.ProbeAsync(clip);
        Assert.True(info is { HasVideo: true, HasAudio: true }, "probe sees video+audio");
        Assert.Close(2.0, info!.DurationSeconds!.Value, "probed duration", 0.3);

        var cache = new CachePaths(Path.Combine(workDir, "cache"));

        // 3) Thumbnail.
        var thumbnails = new ThumbnailService(locator, cache);
        var thumb = await thumbnails.GetThumbnailAsync(clip, 0.5, 160);
        Assert.True(thumb != null && File.Exists(thumb), "thumbnail created");

        // 4) Waveform.
        var waveform = new WaveformService(locator, cache);
        var peaks = await waveform.GetPeaksAsync(clip, 50);
        Assert.True(peaks is { Length: > 50 }, "peaks extracted");
        Assert.True(peaks!.Max() > 0.1f, "sine tone produces visible peaks");

        // 5) Compose a frame with an effect chain.
        var catalog = new EffectCatalog();
        var compositor = new FrameCompositor(new FrameExtractor(locator), new VideoEffectPipeline(catalog));

        var project = new Project();
        var media = new MediaItem
        {
            Name = "clip.mp4", FilePath = clip, Type = MediaType.Video,
            DurationSeconds = 2, HasAudio = true
        };
        project.Media.Items.Add(media);
        var videoTrack = new Track { Name = "V1", Type = TrackType.Video };
        var audioTrack = new Track { Name = "A1", Type = TrackType.Audio };
        project.Tracks.Add(videoTrack);
        project.Tracks.Add(audioTrack);

        var videoEvent = new TimelineEvent { MediaId = media.Id, Start = 0, Duration = 2, SourceOut = 2 };
        videoEvent.Effects.Add(catalog.Find("grayscale")!.CreateInstance());
        videoTrack.Events.Add(videoEvent);
        audioTrack.Events.Add(new TimelineEvent
        {
            MediaId = media.Id, Start = 0, Duration = 2, SourceOut = 2, Volume = 1.2
        });

        var frame = await compositor.ComposeAsync(project, 1.0, 320, 180);
        Assert.Equal(320 * 180 * 4, frame.Bgra.Length, "frame size");
        var grayish = 0;
        for (var i = 0; i < frame.Bgra.Length; i += 4)
            if (Math.Abs(frame.Bgra[i] - frame.Bgra[i + 2]) <= 2) grayish++;
        Assert.True(grayish > 320 * 180 * 0.95, "grayscale effect visible in composed frame");

        // 6) Stream frames continuously (the smooth-playback fast path).
        using (var pipe = StreamingFramePipe.Start(locator, clip, sourceStart: 0.2, playbackRate: 1.0,
                   width: 320, height: 180, fps: 24))
        {
            Assert.True(pipe != null, "pipe started");
            var frames = 0;
            for (var i = 0; i < 5; i++)
            {
                var streamed = await pipe!.ReadFrameAsync(CancellationToken.None);
                if (streamed is null) break;
                Assert.Equal(320 * 180 * 4, streamed.Length, "streamed frame size");
                frames++;
            }
            Assert.True(frames >= 5, "streamed several consecutive frames");
        }

        // 7) Frame cache: repeated extraction at the same position is served from
        //    memory (fast fx-slider re-render) and immune to caller mutation.
        {
            var extractor = new FrameExtractor(locator);
            var first = await extractor.GetFrameAsync(clip, 1.0, 320, 180);
            Assert.True(first != null, "first extraction");
            var expected = (byte[])first!.Bgra.Clone();
            Array.Clear(first.Bgra); // caller mutates its copy (like effects do)

            var timer = System.Diagnostics.Stopwatch.StartNew();
            var second = await extractor.GetFrameAsync(clip, 1.0, 320, 180);
            timer.Stop();

            Assert.True(second != null && second!.Bgra.SequenceEqual(expected),
                "cached frame is pristine despite caller mutation");
            Assert.True(timer.ElapsedMilliseconds < 40,
                $"cache hit should be near-instant (took {timer.ElapsedMilliseconds} ms)");
        }

        // 8) Real-time playback engine: playhead advances with the wall clock,
        //    frames keep arriving, and the run ends exactly at the duration.
        {
            var engine = new VideoEditor.MediaEngine.Playback.PlaybackEngine(
                compositor, new FrameExtractor(locator), new VideoEffectPipeline(catalog), locator);
            var presented = 0;
            var lastTime = -1.0;
            var monotonic = true;

            await engine.RunAsync(project, origin: 0, duration: 1.0, width: 320, height: 180, fps: 24,
                onTime: t => { if (t < lastTime) monotonic = false; lastTime = t; },
                present: (_, w, h) => { presented++; Assert.Equal(320, w, "frame width"); },
                CancellationToken.None);

            Assert.True(monotonic, "playhead time is monotonic");
            Assert.Close(1.0, lastTime, "run ends at the requested duration", 0.05);
            Assert.True(presented >= 5, $"frames were presented continuously (got {presented})");
        }

        // 9) Export the yellow-range selection [0.5, 1.5) and verify the result.
        project.ExportRange = new TimeRange { Start = 0.5, End = 1.5 };
        var output = Path.Combine(workDir, "out.mp4");
        var export = new ExportService(locator, compositor, catalog);
        await export.ExportAsync(project, new ExportSettings
        {
            OutputPath = output, Width = 320, Height = 180, FrameRate = 24,
            AudioSampleRate = 48000, Range = project.ExportRange
        });

        Assert.True(File.Exists(output), "export file exists");
        var exported = await probe.ProbeAsync(output);
        Assert.True(exported is { HasVideo: true, HasAudio: true }, "export has video+audio");
        Assert.Close(1.0, exported!.DurationSeconds!.Value, "export duration equals range", 0.25);
    }
}
