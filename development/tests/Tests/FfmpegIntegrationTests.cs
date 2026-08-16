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

        // 6b) The sequential export renderer matches the per-frame compositor
        //     (decode timing at frame boundaries may differ by one source frame,
        //     so one outlier out of five is tolerated).
        {
            using var sequential = new SequentialCompositor(locator, compositor);
            var canvas = new byte[320 * 180 * 4];
            var closeFrames = 0;
            for (var i = 0; i < 5; i++)
            {
                var t = 0.5 + i / 24.0;
                await sequential.RenderAsync(project, t, i, 24, canvas, 320, 180, CancellationToken.None);
                var reference = await compositor.ComposeAsync(project, t, 320, 180);
                long difference = 0;
                for (var b = 0; b < canvas.Length; b++)
                    difference += Math.Abs(canvas[b] - reference.Bgra[b]);
                if (difference / (double)canvas.Length < 8) closeFrames++;
            }
            Assert.True(closeFrames >= 4,
                $"sequential renderer stays close to the reference ({closeFrames}/5 frames)");
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

        // 8b) Overlapping layers must play through the sequential renderer, not
        //     one seek+decode per layer per frame. Measured directly: a run of
        //     consecutive frames has to be far cheaper than the same frames
        //     composed independently, or preview playback stutters again.
        {
            var overlayTrack = new Track { Name = "V2", Type = TrackType.Overlay };
            overlayTrack.Events.Add(new TimelineEvent
            {
                MediaId = media.Id, Start = 0, Duration = 2, SourceOut = 2, Opacity = 0.5
            });
            project.Tracks.Add(overlayTrack);

            Assert.True(FrameCompositor.FindSingleVisualLayer(project, 0.5) is null,
                "two visual layers force the overlap path");

            const int frames = 6;
            var canvas = new byte[320 * 180 * 4];
            using (var sequential = new SequentialCompositor(locator, compositor))
            {
                // Frame 0 starts the decoders; playback pays that once per run.
                await sequential.RenderAsync(project, 0.5, 0, 24, canvas, 320, 180, CancellationToken.None);

                var sequentialTimer = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 1; i <= frames; i++)
                    await sequential.RenderAsync(
                        project, 0.5 + i / 24.0, i, 24, canvas, 320, 180, CancellationToken.None);
                sequentialTimer.Stop();

                var perFrameTimer = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 1; i <= frames; i++)
                    await compositor.ComposeAsync(project, 0.5 + i / 24.0, 320, 180);
                perFrameTimer.Stop();

                Assert.True(sequentialTimer.ElapsedMilliseconds * 2 < perFrameTimer.ElapsedMilliseconds,
                    $"sequential overlap rendering beats per-frame composition " +
                    $"({sequentialTimer.ElapsedMilliseconds} ms vs {perFrameTimer.ElapsedMilliseconds} ms for {frames} frames)");
            }

            // And the engine keeps presenting while the layers overlap.
            var overlapEngine = new VideoEditor.MediaEngine.Playback.PlaybackEngine(
                compositor, new FrameExtractor(locator), new VideoEffectPipeline(catalog), locator);
            var overlapFrames = 0;
            await overlapEngine.RunAsync(project, origin: 0, duration: 1.0, width: 320, height: 180, fps: 24,
                onTime: _ => { },
                present: (_, _, _) => overlapFrames++,
                CancellationToken.None);
            Assert.True(overlapFrames >= 5,
                $"overlapping layers still present frames continuously (got {overlapFrames})");

            project.Tracks.Remove(overlayTrack);
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

        // 10) Audio-only export (MP3) from the same range.
        var mp3Output = Path.Combine(workDir, "out.mp3");
        await export.ExportAsync(project, new ExportSettings
        {
            OutputPath = mp3Output, Format = ExportFormat.Mp3,
            AudioSampleRate = 48000, Range = project.ExportRange
        });
        var mp3Probe = await probe.ProbeAsync(mp3Output);
        Assert.True(mp3Probe is { HasAudio: true, HasVideo: false }, "mp3 is audio-only");
    }
}
