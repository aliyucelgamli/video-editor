using VideoEditor.Application.Effects;
using VideoEditor.Domain;
using VideoEditor.MediaEngine;
using VideoEditor.MediaEngine.Effects;
using VideoEditor.MediaEngine.Export;
using VideoEditor.MediaEngine.Ffmpeg;
using VideoEditor.MediaEngine.Frames;
using VideoEditor.MediaEngine.Waveform;

namespace VideoEditor.Tests;

public static class MediaEngineTests
{
    private static void CreateEntry(
        System.IO.Compression.ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    public static void Register()
    {
        TestRunner.Add("Probe: parses ffprobe JSON (duration, streams, rational fps)", () =>
        {
            const string json = """
            {
              "streams": [
                { "codec_type": "video", "width": 1920, "height": 1080, "r_frame_rate": "30000/1001" },
                { "codec_type": "audio", "sample_rate": "48000" }
              ],
              "format": { "duration": "12.480000" }
            }
            """;
            var info = MediaProbe.ParseProbeJson(json);
            Assert.Close(12.48, info.DurationSeconds!.Value, "duration");
            Assert.True(info.HasVideo && info.HasAudio, "streams detected");
            Assert.Equal(1920, info.Width!.Value, "width");
            Assert.Close(29.97, info.FrameRate!.Value, "fps", 0.01);
            Assert.Equal(48000, info.AudioSampleRate!.Value, "sample rate");
        });

        TestRunner.Add("Probe: cover art is not treated as a video stream", () =>
        {
            const string json = """
            {
              "streams": [
                { "codec_type": "audio", "sample_rate": "44100" },
                { "codec_type": "video", "width": 500, "height": 500, "disposition": { "attached_pic": 1 } }
              ],
              "format": { "duration": "180.0" }
            }
            """;
            var info = MediaProbe.ParseProbeJson(json);
            Assert.False(info.HasVideo, "attached_pic skipped");
            Assert.True(info.HasAudio);
        });

        TestRunner.Add("Waveform: peaks reduce PCM correctly", () =>
        {
            // 4 samples: 0, +16384, -32768, +8192 → two buckets of two.
            var pcm = new byte[8];
            BitConverter.GetBytes((short)0).CopyTo(pcm, 0);
            BitConverter.GetBytes((short)16384).CopyTo(pcm, 2);
            BitConverter.GetBytes((short)-32768).CopyTo(pcm, 4);
            BitConverter.GetBytes((short)8192).CopyTo(pcm, 6);

            var peaks = WaveformService.ComputePeaks(pcm, 2);
            Assert.Equal(2, peaks.Length, "bucket count");
            Assert.Close(0.5, peaks[0], "bucket 1", 0.01);
            Assert.Close(1.0, peaks[1], "bucket 2", 0.01);
        });

        TestRunner.Add("Kernels: grayscale converges channels, invert flips", () =>
        {
            var frame = new byte[] { 200, 100, 50, 255 }; // one BGRA pixel
            new GrayscaleKernel().Apply(frame, 1, 1, new Dictionary<string, double> { ["amount"] = 1 });
            Assert.True(Math.Abs(frame[0] - frame[1]) <= 1 && Math.Abs(frame[1] - frame[2]) <= 1,
                "channels should be (nearly) equal after grayscale");

            var pixel = new byte[] { 0, 128, 255, 255 };
            new InvertKernel().Apply(pixel, 1, 1, new Dictionary<string, double> { ["amount"] = 1 });
            Assert.Equal((byte)255, pixel[0], "B inverted");
            Assert.Equal((byte)127, pixel[1], "G inverted");
            Assert.Equal((byte)0, pixel[2], "R inverted");
        });

        TestRunner.Add("Kernels: temperature warms red / cools blue", () =>
        {
            var pixel = new byte[] { 100, 100, 100, 255 };
            new TemperatureKernel().Apply(pixel, 1, 1, new Dictionary<string, double> { ["amount"] = 1 });
            Assert.True(pixel[2] > 100, "red up");
            Assert.True(pixel[0] < 100, "blue down");
        });

        TestRunner.Add("Pipeline: applies chain through catalog, skips disabled", () =>
        {
            var catalog = new EffectCatalog();
            var pipeline = new VideoEffectPipeline(catalog);
            var frame = new byte[] { 200, 100, 50, 255 };

            var grayscale = catalog.Find("grayscale")!.CreateInstance();
            var disabled = catalog.Find("invert")!.CreateInstance();
            disabled.Enabled = false;

            pipeline.Apply(frame, 1, 1, new[] { grayscale, disabled });
            Assert.True(Math.Abs(frame[0] - frame[2]) <= 1, "grayscale applied");
            Assert.True(frame[1] > 60 && frame[1] < 160, "invert skipped");
        });

        TestRunner.Add("Glitch: deterministic per time, animates across time", () =>
        {
            var catalog = new EffectCatalog();
            var pipeline = new VideoEffectPipeline(catalog);
            var glitch = catalog.Find("glitch")!.CreateInstance();
            glitch.Parameters["amount"] = 1.0;

            byte[] MakeFrame()
            {
                var frame = new byte[32 * 32 * 4];
                for (var i = 0; i < frame.Length; i += 4)
                {
                    frame[i] = (byte)(i % 251);
                    frame[i + 2] = (byte)(i % 127);
                    frame[i + 3] = 255;
                }
                return frame;
            }

            var a = MakeFrame();
            var b = MakeFrame();
            var c = MakeFrame();
            var original = MakeFrame();

            pipeline.Apply(a, 32, 32, new[] { glitch }, timeSeconds: 0.4);
            pipeline.Apply(b, 32, 32, new[] { glitch }, timeSeconds: 0.4);
            pipeline.Apply(c, 32, 32, new[] { glitch }, timeSeconds: 2.7);

            Assert.True(a.SequenceEqual(b), "same time → identical frame (deterministic)");
            Assert.False(a.SequenceEqual(original), "glitch changes the frame");
            Assert.False(a.SequenceEqual(c), "different time → different glitch pattern");
        });

        TestRunner.Add("Audio filters: helium builds pitch chain, volume applied", () =>
        {
            var catalog = new EffectCatalog();
            var evt = new TimelineEvent { Name = "voice", Duration = 10, Volume = 1.5 };
            evt.Effects.Add(catalog.Find("helium")!.CreateInstance());

            var filter = AudioFilterGraphBuilder.BuildEventFilter(evt, catalog, trackVolume: 1.0, sampleRate: 48000);
            Assert.True(filter.Contains("asetrate=48000*1.6"), "asetrate present: " + filter);
            Assert.True(filter.Contains("atempo="), "atempo present");
            Assert.True(filter.Contains("volume=1.5"), "volume present");
        });

        TestRunner.Add("Audio filters: tempo chain stays within atempo limits", () =>
        {
            foreach (var tempo in new[] { 0.2, 0.5, 1.0, 1.7, 3.0, 6.0 })
            {
                var chain = AudioFilterGraphBuilder.TempoChain(tempo);
                var product = 1.0;
                foreach (var step in chain)
                {
                    var value = double.Parse(step["atempo=".Length..],
                        System.Globalization.CultureInfo.InvariantCulture);
                    Assert.True(value >= 0.5 - 1e-9 && value <= 2.0 + 1e-9, $"step in range for {tempo}");
                    product *= value;
                }
                if (Math.Abs(tempo - 1.0) > 0.001)
                    Assert.Close(tempo, product, $"chain product for {tempo}", 0.001);
            }
        });

        TestRunner.Add("Export: audio mix plan honors range, mute/solo and delay", () =>
        {
            var project = new Project();
            var media = new MediaItem { Name = "song.mp3", FilePath = "song.mp3", Type = MediaType.Audio };
            project.Media.Items.Add(media);

            var a1 = new Track { Name = "A1", Type = TrackType.Audio };
            var a2 = new Track { Name = "A2", Type = TrackType.Audio, Muted = true };
            project.Tracks.Add(a1);
            project.Tracks.Add(a2);

            a1.Events.Add(new TimelineEvent { MediaId = media.Id, Start = 2, Duration = 10, SourceIn = 1, SourceOut = 11 });
            a2.Events.Add(new TimelineEvent { MediaId = media.Id, Start = 0, Duration = 4 });

            var range = new TimeRange { Start = 4, End = 8 };
            var segments = AudioMixPlanner.CollectSegments(project, range);
            Assert.Equal(1, segments.Count, "muted track excluded");
            Assert.Close(3, segments[0].SourceIn, "source in shifted by range clip");
            Assert.Close(4, segments[0].SourceDuration, "clipped duration");
            Assert.Close(0, segments[0].TimelineOffset, "offset");

            var arguments = AudioMixPlanner.BuildMixArguments(
                project, new EffectCatalog(), range, 48000, "out.wav");
            Assert.True(arguments.Contains("song.mp3"), "input included");
            Assert.True(arguments.Any(a => a.Contains("atrim=0:4")), "trimmed to range duration");
        });

        TestRunner.Add("Export: empty timeline mixes silence", () =>
        {
            var arguments = AudioMixPlanner.BuildMixArguments(
                new Project(), new EffectCatalog(), new TimeRange { Start = 0, End = 3 }, 48000, "out.wav");
            Assert.True(arguments.Any(a => a.Contains("anullsrc")), "silent source used");
        });

        TestRunner.Add("Transform: identity is a no-op, scale/position remap pixels", () =>
        {
            var transform = new Transform2D();
            var frame = new byte[4 * 4 * 4];
            Assert.True(ReferenceEquals(frame,
                FrameCompositor.ApplyTransform(frame, 4, 4, transform, 1)), "identity returns same array");

            // Single bright pixel at (1,1) on a 4×4 canvas, scaled 2× around center:
            // inverse mapping puts source (1,1) at destination (0,0)…(1,1) region.
            frame[(1 * 4 + 1) * 4 + 2] = 255; // red
            frame[(1 * 4 + 1) * 4 + 3] = 255; // alpha
            var scaled = FrameCompositor.ApplyTransform(
                frame, 4, 4, new Transform2D { ScaleX = 2, ScaleY = 2 }, 1);
            Assert.True(scaled[(1 * 4 + 1) * 4 + 3] > 0, "scaled pixel covers near-center area");

            // Position offset: shift right by 2 px moves content toward +x.
            var moved = FrameCompositor.ApplyTransform(
                frame, 4, 4, new Transform2D { PositionX = 2 }, 1);
            Assert.True(moved[(1 * 4 + 3) * 4 + 3] > 0, "pixel moved right");
            Assert.Equal(0, (int)moved[(1 * 4 + 1) * 4 + 3], "old spot now transparent");

            // Position scale halves offsets for half-size canvases.
            var halfScaleMoved = FrameCompositor.ApplyTransform(
                frame, 4, 4, new Transform2D { PositionX = 4 }, 0.5);
            Assert.True(halfScaleMoved[(1 * 4 + 3) * 4 + 3] > 0, "project-pixel offset scaled to canvas");
        });

        TestRunner.Add("Export: encoder arguments follow the chosen format", () =>
        {
            List<string> ArgsFor(ExportFormat format) => ExportService.BuildEncoderArguments(
                new ExportSettings { Format = format, OutputPath = "out", Crf = 20 }, "mix.wav", 320, 180);

            Assert.True(ArgsFor(ExportFormat.Mp4H264).Contains("libx264"), "h264");
            Assert.True(ArgsFor(ExportFormat.Mp4Hevc).Contains("libx265"), "h265");
            var webm = ArgsFor(ExportFormat.WebMVp9);
            Assert.True(webm.Contains("libvpx-vp9") && webm.Contains("libopus"), "vp9+opus");
            Assert.True(webm.Contains("-cpu-used"), "vp9 uses the fast deadline knob");
        });

        TestRunner.Add("Export: GPU encoder arguments per vendor, CPU fallback otherwise", () =>
        {
            List<string> ArgsFor(ExportFormat format, string? encoder) => ExportService.BuildEncoderArguments(
                new ExportSettings { Format = format, OutputPath = "out", Crf = 21, VideoEncoder = encoder },
                "mix.wav", 320, 180);

            var nvenc = ArgsFor(ExportFormat.Mp4H264, "h264_nvenc");
            Assert.True(nvenc.Contains("h264_nvenc") && nvenc.Contains("-cq"), "nvenc uses cq rate control");
            Assert.False(nvenc.Contains("libx264"), "no double codec with nvenc");

            var hevcNvenc = ArgsFor(ExportFormat.Mp4Hevc, "hevc_nvenc");
            Assert.True(hevcNvenc.Contains("hevc_nvenc") && hevcNvenc.Contains("hvc1"),
                "hevc keeps the hvc1 tag on the GPU path");

            Assert.True(ArgsFor(ExportFormat.Mp4H264, "h264_qsv").Contains("-global_quality"),
                "quick sync uses global_quality");
            var amf = ArgsFor(ExportFormat.Mp4H264, "h264_amf");
            Assert.True(amf.Contains("-qp_i") && amf.Contains("-qp_p"), "amf uses cqp");

            Assert.True(ArgsFor(ExportFormat.Mp4H264, "h264_imaginary").Contains("libx264"),
                "unknown encoder name falls back to the CPU encoder");
            Assert.True(ArgsFor(ExportFormat.Mp4H264, null).Contains("libx264"),
                "no resolved encoder means CPU");
        });

        TestRunner.Add("Export: GPU encoder detection parses listings and ranks candidates", () =>
        {
            var listing = string.Join('\n',
                "Encoders:",
                " V..... = Video",
                " A..... = Audio",
                " ------",
                " V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC",
                " V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)",
                " V....D h264_amf             AMD AMF H.264 Encoder (codec h264)",
                " A....D aac                  AAC (Advanced Audio Coding)");
            var names = HardwareEncoders.ParseEncoderNames(listing);
            Assert.True(names.Contains("h264_nvenc") && names.Contains("libx264") && names.Contains("aac"),
                "encoder names parsed");
            Assert.False(names.Contains("=") || names.Contains("------"), "legend/header lines skipped");

            Assert.Equal("h264_nvenc", HardwareEncoders.CandidatesFor(ExportFormat.Mp4H264)[0].Encoder,
                "nvenc is the preferred h264 GPU encoder");
            Assert.Equal("hevc_nvenc", HardwareEncoders.CandidatesFor(ExportFormat.Mp4Hevc)[0].Encoder,
                "nvenc is the preferred hevc GPU encoder");
            Assert.Equal(0, HardwareEncoders.CandidatesFor(ExportFormat.Mp3).Count,
                "audio-only formats have no GPU path");
            Assert.Equal(0, HardwareEncoders.CandidatesFor(ExportFormat.WebMVp9).Count,
                "vp9 stays on the CPU encoder");
        });

        TestRunner.Add("Compositor: fade factor ramps in and out", () =>
        {
            var evt = new TimelineEvent { Start = 0, Duration = 10, FadeInDuration = 2, FadeOutDuration = 2 };
            Assert.Close(0.5, FrameCompositor.FadeFactor(evt, 1), "fade in midpoint");
            Assert.Close(1.0, FrameCompositor.FadeFactor(evt, 5), "middle");
            Assert.Close(0.5, FrameCompositor.FadeFactor(evt, 9), "fade out midpoint");
        });

        TestRunner.Add("Installer: extracts ffmpeg/ffprobe from any zip layout", () =>
        {
            var work = Path.Combine(Path.GetTempPath(), $"ffzip_{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, "build.zip");
            var target = Path.Combine(work, "tools");
            try
            {
                using (var archive = System.IO.Compression.ZipFile.Open(
                    zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    CreateEntry(archive, "ffmpeg-7.1-essentials_build/bin/ffmpeg.exe", "fake-ffmpeg");
                    CreateEntry(archive, "ffmpeg-7.1-essentials_build/bin/ffprobe.exe", "fake-ffprobe");
                    CreateEntry(archive, "ffmpeg-7.1-essentials_build/README.txt", "readme");
                }

                FfmpegInstaller.ExtractPortableBuild(zipPath, target);
                Assert.True(File.Exists(Path.Combine(target, "ffmpeg.exe")), "ffmpeg extracted");
                Assert.True(File.Exists(Path.Combine(target, "ffprobe.exe")), "ffprobe extracted");
                Assert.False(File.Exists(Path.Combine(target, "README.txt")), "extras skipped");
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        });

        TestRunner.Add("Installer: rejects archives without ffmpeg", () =>
        {
            var work = Path.Combine(Path.GetTempPath(), $"ffzip_{Guid.NewGuid():N}");
            Directory.CreateDirectory(work);
            var zipPath = Path.Combine(work, "empty.zip");
            try
            {
                using (var archive = System.IO.Compression.ZipFile.Open(
                    zipPath, System.IO.Compression.ZipArchiveMode.Create))
                {
                    CreateEntry(archive, "notes.txt", "nothing here");
                }
                Assert.Throws<InvalidOperationException>(() =>
                    FfmpegInstaller.ExtractPortableBuild(zipPath, Path.Combine(work, "tools")));
            }
            finally
            {
                Directory.Delete(work, recursive: true);
            }
        });

        TestRunner.Add("Cache: keys are stable and variant-sensitive", () =>
        {
            var key1 = CachePaths.KeyFor("missing-file.mp4", "thumb", 1.0, 160);
            var key2 = CachePaths.KeyFor("missing-file.mp4", "thumb", 1.0, 160);
            var key3 = CachePaths.KeyFor("missing-file.mp4", "thumb", 2.0, 160);
            Assert.Equal(key1, key2, "same inputs → same key");
            Assert.True(key1 != key3, "different time → different key");
        });
    }
}
