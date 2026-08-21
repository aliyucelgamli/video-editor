using VideoEditor.Application.Effects;
using VideoEditor.Domain;
using VideoEditor.Domain.Sound;
using VideoEditor.MediaEngine.Audio;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.Tests;

/// <summary>
/// The sound editor's model and its FFmpeg command construction. The ffmpeg
/// round-trip at the end generates its own tone file and self-skips when
/// ffmpeg is missing.
/// </summary>
public static class SoundEditorTests
{
    public static void Register()
    {
        // ---------- Session model ----------

        TestRunner.Add("Sound: a new session is the whole file, untouched", () =>
        {
            var session = SoundEditSession.ForFile(@"C:\a.wav", "a.wav", 12.5);
            Assert.Equal(1, session.Segments.Count, "segment count");
            Assert.Close(12.5, session.OutputDuration, "output duration");
            Assert.Close(0, session.Segments[0].SourceIn, "source in");
            Assert.Close(12.5, session.ToSourceTime(12.5), "past the end maps to the file end");
        });

        TestRunner.Add("Sound: split cuts one piece into two adjoining pieces", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 10);
            Assert.True(session.SplitAt(4), "split at 4s");
            Assert.Equal(2, session.Segments.Count, "segment count");
            Assert.Close(4, session.Segments[0].SourceOut, "first piece ends at the cut");
            Assert.Close(4, session.Segments[1].SourceIn, "second piece starts at the cut");
            Assert.Close(10, session.OutputDuration, "a split changes nothing audible");
        });

        TestRunner.Add("Sound: a split at the very edge is refused", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 10);
            Assert.False(session.SplitAt(0), "no sliver at the start");
            Assert.False(session.SplitAt(10), "past the end");
            Assert.False(session.SplitAt(9.999), "no sliver at the end");
            Assert.Equal(1, session.Segments.Count, "still one piece");
        });

        TestRunner.Add("Sound: deleting a middle range closes the gap", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 10);
            Assert.True(session.RemoveRange(3, 5), "removed 3–5s");
            Assert.Close(8, session.OutputDuration, "output duration");
            // Output second 3 now plays source second 5.
            Assert.Close(5, session.ToSourceTime(3.0), "mapping after the cut");
            Assert.Close(1, session.ToSourceTime(1.0), "mapping before the cut is unchanged");
        });

        TestRunner.Add("Sound: trim to selection keeps only the selection", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 10);
            Assert.True(session.TrimTo(2, 6), "trimmed to 2–6s");
            Assert.Close(4, session.OutputDuration, "output duration");
            Assert.Close(2, session.ToSourceTime(0), "starts at the selection");
            Assert.Close(5.5, session.ToSourceTime(3.5), "ends at the selection");
        });

        TestRunner.Add("Sound: trimming a session with several pieces keeps their order", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 12);
            session.SplitAt(4);
            session.SplitAt(8);
            Assert.Equal(3, session.Segments.Count, "three pieces");

            var middleId = session.Segments[1].Id;
            Assert.True(session.MoveSegment(middleId, -1), "middle piece moved up");
            Assert.Equal(middleId, session.Segments[0].Id, "moved piece is first");
            Assert.Close(4, session.ToSourceTime(0), "the reordered piece plays first");
            Assert.Close(12, session.OutputDuration, "reordering keeps every second");
        });

        TestRunner.Add("Sound: an empty range is rejected, a full one clears the clip", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 10);
            Assert.False(session.RemoveRange(4, 4), "zero-length range");
            Assert.False(session.TrimTo(2, 2.001), "sub-threshold range");
            Assert.True(session.RemoveRange(0, 10), "everything");
            Assert.True(session.IsEmpty, "nothing left");
        });

        TestRunner.Add("Sound: fades are clamped and never overlap", () =>
        {
            var segment = new SoundSegment { SourceIn = 0, SourceOut = 2, FadeIn = 5, FadeOut = 5 };
            segment.ClampFades();
            Assert.Close(2, segment.FadeIn, "fade in fills the piece");
            Assert.Close(0, segment.FadeOut, "fade out has no room left");

            var eased = new SoundSegment { SourceIn = 0, SourceOut = 4, FadeIn = 2, FadeOut = 1 };
            Assert.Close(0, eased.FadeFactorAt(0), "silent at the start");
            Assert.Close(0.5, eased.FadeFactorAt(1), "halfway up a linear fade in");
            Assert.Close(1, eased.FadeFactorAt(2.5), "full level between the fades");
            Assert.Close(0.5, eased.FadeFactorAt(3.5), "halfway down the fade out");
        });

        TestRunner.Add("Sound: muting a piece silences it without losing its gain", () =>
        {
            var segment = new SoundSegment { SourceIn = 0, SourceOut = 1, Gain = 1.5, Muted = true };
            Assert.Close(0, segment.EffectiveGain, "muted gain");
            segment.Muted = false;
            Assert.Close(1.5, segment.EffectiveGain, "gain came back");
            segment.Gain = 99;
            Assert.Close(VolumeLimits.Max, segment.EffectiveGain, "gain is clamped to the shared limit");
        });

        TestRunner.Add("Sound: a copy shares nothing mutable but keeps identities", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 8);
            session.SplitAt(4);
            session.Effects.Add(new EffectInstance { Type = "echo", Parameters = { ["delay"] = 200 } });

            var copy = session.Copy();
            Assert.Equal(session.Segments[0].Id, copy.Segments[0].Id, "segment identity survives");
            Assert.Equal(session.Effects[0].Id, copy.Effects[0].Id, "effect identity survives");

            copy.Segments[0].Gain = 0.25;
            copy.Effects[0].Parameters["delay"] = 900;
            Assert.Close(1.0, session.Segments[0].Gain, "the original's gain is untouched");
            Assert.Close(200, session.Effects[0].Parameters["delay"], "the original's parameter is untouched");
        });

        TestRunner.Add("Sound: past the end maps to where the last piece stops", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 10);
            Assert.Close(10, session.ToSourceTime(10), "an untouched clip ends where the file does");

            session.TrimTo(0, 3);
            Assert.Close(3, session.ToSourceTime(3), "after a trim it ends where the trim did");
            Assert.Close(3, session.ToSourceTime(99), "and well past the end too");
        });

        // ---------- Command construction ----------

        TestRunner.Add("Sound export: one input per piece, joined with concat", () =>
        {
            var catalog = new EffectCatalog();
            var session = SoundEditSession.ForFile(@"C:\snd\hit.wav", "hit.wav", 6);
            session.SplitAt(2);
            session.RemoveRange(0, 1); // now two pieces: 1–2s and 2–6s

            var inputs = AudioClipPlanner.BuildInputArguments(session);
            Assert.Equal(12, inputs.Count, "two -ss/-t/-i triples");
            Assert.Equal("-ss", inputs[0], "input seek first");
            Assert.Equal(@"C:\snd\hit.wav", inputs[5], "same source file for both pieces");

            var graph = Graph(session, catalog, new AudioChainOptions { SampleRate = 44100 });
            Assert.True(graph.Contains("concat=n=2:v=0:a=1"), "two pieces are concatenated");
            Assert.True(graph.Contains("[out]"), "the graph ends on the out label");
            Assert.False(graph.Contains("volume="), "no gain change means no volume filter");
        });

        TestRunner.Add("Sound export: a single piece needs no concat", () =>
        {
            var graph = Graph(SoundEditSession.ForFile("a.wav", "a", 3), new EffectCatalog(),
                new AudioChainOptions { SampleRate = 48000 });
            Assert.False(graph.Contains("concat"), "nothing to join");
            Assert.True(graph.Contains("aresample=48000"), "the requested sample rate is applied");
        });

        TestRunner.Add("Sound export: gain, fades and the master chain land in the graph", () =>
        {
            var catalog = new EffectCatalog();
            var session = SoundEditSession.ForFile("a.wav", "a", 5);
            session.Segments[0].Gain = 0.5;
            session.Segments[0].FadeIn = 1;
            session.Segments[0].FadeOut = 2;
            session.Segments[0].FadeOutEasing = EasingType.InOutSine;
            session.MasterGain = 1.25;
            session.Effects.Add(new EffectInstance { Type = "echo", Parameters = { ["delay"] = 300, ["decay"] = 0.5 } });

            var graph = Graph(session, catalog, new AudioChainOptions { SampleRate = 44100 });
            Assert.True(graph.Contains("volume=0.5"), "segment gain");
            Assert.True(graph.Contains("afade=t=in:st=0:d=1:curve=tri"), "linear fade in");
            Assert.True(graph.Contains("afade=t=out:st=3:d=2:curve=hsin"), "eased fade out");
            Assert.True(graph.Contains("aecho="), "master effect chain");
            Assert.True(graph.Contains("volume=1.25"), "master gain");
        });

        TestRunner.Add("Sound export: a disabled effect is left out", () =>
        {
            var session = SoundEditSession.ForFile("a.wav", "a", 2);
            session.Effects.Add(new EffectInstance { Type = "echo", Enabled = false });
            var graph = Graph(session, new EffectCatalog(), new AudioChainOptions { SampleRate = 44100 });
            Assert.False(graph.Contains("aecho"), "disabled effects do not render");
        });

        TestRunner.Add("Sound export: silence trim runs on both ends", () =>
        {
            var graph = Graph(SoundEditSession.ForFile("a.wav", "a", 4), new EffectCatalog(),
                new AudioChainOptions { TrimSilence = true, SilenceThresholdDb = -40 });
            var reverses = graph.Split("areverse").Length - 1;
            Assert.Equal(2, reverses, "reversed once to trim the tail and once to restore");
            Assert.True(graph.Contains("start_threshold=-40dB"), "the chosen threshold is used");
            // start_duration buffers non-silence and then THROWS IT AWAY, so a
            // non-zero value clips the first transient off every export.
            Assert.True(graph.Contains("start_duration=0:"), "no non-silence is buffered away");
            Assert.True(graph.Contains("start_silence=0.05"), "a short edge silence is kept");
        });

        TestRunner.Add("Sound export: codec flags follow the format", () =>
        {
            string Flags(AudioExportFormat format, Action<AudioExportSettings>? tweak = null)
            {
                var settings = new AudioExportSettings { Format = format, Bitrate = 160 };
                tweak?.Invoke(settings);
                return string.Join(" ", AudioClipPlanner.CodecArguments(settings));
            }

            Assert.Equal("-c:a libmp3lame -b:a 160k", Flags(AudioExportFormat.Mp3), "mp3");
            Assert.Equal("-c:a libvorbis -b:a 160k", Flags(AudioExportFormat.OggVorbis), "ogg");
            Assert.Equal("-c:a libopus -b:a 160k", Flags(AudioExportFormat.Opus), "opus");
            Assert.Equal("-c:a aac -b:a 160k", Flags(AudioExportFormat.M4aAac), "m4a");
            Assert.Equal("-c:a flac -compression_level 8",
                Flags(AudioExportFormat.Flac, s => s.FlacCompression = 8), "flac");
            Assert.Equal("-c:a pcm_s24le",
                Flags(AudioExportFormat.Wav, s => s.BitDepth = WavBitDepth.Pcm24), "24-bit wav");
        });

        TestRunner.Add("Sound export: Opus only gets sample rates it accepts", () =>
        {
            Assert.Equal(48000, AudioExportFormat.Opus.ClampSampleRate(44100), "44.1k → 48k for opus");
            Assert.Equal(24000, AudioExportFormat.Opus.ClampSampleRate(22050), "22k → 24k for opus");
            Assert.Equal(44100, AudioExportFormat.Mp3.ClampSampleRate(44100), "every other codec is left alone");

            var settings = new AudioExportSettings { Format = AudioExportFormat.Opus, SampleRate = 44100 };
            var arguments = AudioClipPlanner.BuildExportArguments(
                SoundEditSession.ForFile("a.wav", "a", 1), new EffectCatalog(), settings);
            var rateIndex = arguments.IndexOf("-ar");
            Assert.Equal("48000", arguments[rateIndex + 1], "the clamped rate reaches ffmpeg");
        });

        TestRunner.Add("Sound export: the peak pass measures without normalizing", () =>
        {
            var settings = new AudioExportSettings { Normalize = AudioNormalizeMode.Peak };
            var arguments = AudioClipPlanner.BuildMeasureArguments(
                SoundEditSession.ForFile("a.wav", "a", 1), new EffectCatalog(), settings);
            var graph = arguments[arguments.IndexOf("-filter_complex") + 1];
            Assert.True(graph.Contains("volumedetect"), "the peak is measured");
            Assert.False(graph.Contains("loudnorm"), "the measuring pass changes no levels");
            Assert.True(arguments.Contains("null"), "and writes nowhere");
        });

        TestRunner.Add("Sound export: loudness normalize is a single-pass filter", () =>
        {
            var graph = Graph(SoundEditSession.ForFile("a.wav", "a", 4), new EffectCatalog(),
                new AudioChainOptions
                {
                    Normalize = AudioNormalizeMode.Loudness, LoudnessTargetLufs = -14
                });
            Assert.True(graph.Contains("loudnorm=I=-14"), "the chosen LUFS target is used");
            Assert.True(graph.Contains("TP=-1"), "with a true-peak ceiling ffmpeg accepts");
        });

        TestRunner.Add("Sound export: ffmpeg's peak and progress lines are parsed", () =>
        {
            Assert.Close(-3.5, AudioClipPlanner.ParseMaxVolumeDb(
                "[Parsed_volumedetect_0 @ 0x1] max_volume: -3.5 dB\n")!.Value, "peak level");
            Assert.True(AudioClipPlanner.ParseMaxVolumeDb("nothing here") is null, "no peak line");

            Assert.Close(1.5, AudioClipPlanner.ParseProgressSeconds("out_time_us=1500000")!.Value, "progress");
            Assert.True(AudioClipPlanner.ParseProgressSeconds("frame=12") is null, "unrelated line");
        });

        TestRunner.Add("Sound export: an empty clip refuses to build a command", () =>
            Assert.Throws<InvalidOperationException>(() =>
                Graph(new SoundEditSession(), new EffectCatalog(), new AudioChainOptions())));

        TestRunner.Add("Sound preview: the audition runs the export's own chain", () =>
        {
            var catalog = new EffectCatalog();
            var session = SoundEditSession.ForFile("a.wav", "a", 30);
            session.Segments[0].FadeIn = 4;
            session.Segments[0].FadeOut = 4;

            var settings = new AudioExportSettings
            {
                Format = AudioExportFormat.OggVorbis,
                Normalize = AudioNormalizeMode.Loudness,
                TrimSilence = true,
                SampleRate = 48000,
                Channels = 1
            };

            var export = AudioClipPlanner.BuildExportArguments(session, catalog, settings);
            var preview = AudioClipPlanner.BuildPreviewArguments(
                session, catalog, settings, "out.wav", fromOutputTime: 6, maxSeconds: 10);

            var exportGraph = export[export.IndexOf("-filter_complex") + 1];
            var previewGraph = preview[preview.IndexOf("-filter_complex") + 1];

            // The audition is the export's graph plus a window — nothing removed,
            // or the user would hear something the file will not contain.
            Assert.True(previewGraph.Contains("afade=t=in:st=0:d=4"), "the fade in survives");
            Assert.True(previewGraph.Contains("afade=t=out"), "the fade out survives");
            Assert.True(previewGraph.Contains("areverse"), "the silence trim survives");
            Assert.True(exportGraph.Contains("loudnorm=I=-16"), "the export normalizes");
            Assert.True(previewGraph.Contains("atrim=start=6:end=16"), "windowed to the audition");
            Assert.True(previewGraph.Contains("asetpts"), "and restamped to start at zero");
            Assert.False(exportGraph.Contains("atrim"), "the export itself is not windowed");
            Assert.Equal("48000", preview[preview.IndexOf("-ar") + 1], "the export's sample rate");
            Assert.Equal("1", preview[preview.IndexOf("-ac") + 1], "the export's channel count");
            Assert.Close(30, session.OutputDuration, "and the model is left alone");
        });

        TestRunner.Add("Sound preview: normalization is the one stage left out", () =>
        {
            // A peak normalize needs a second pass, and single-pass loudnorm is
            // adaptive — neither can be windowed exactly, so an audition plays
            // the edit at its natural level and the file gets the level.
            foreach (var mode in new[] { AudioNormalizeMode.Peak, AudioNormalizeMode.Loudness })
            {
                var settings = new AudioExportSettings { Normalize = mode };
                var preview = AudioClipPlanner.BuildPreviewArguments(
                    SoundEditSession.ForFile("a.wav", "a", 5), new EffectCatalog(), settings,
                    "out.wav", 0, 5);
                var graph = preview[preview.IndexOf("-filter_complex") + 1];
                Assert.False(graph.Contains("volumedetect"), $"{mode}: no measuring in an audition");
                Assert.False(graph.Contains("loudnorm"), $"{mode}: no normalization in an audition");
            }
        });

        TestRunner.Add("Sound export: size estimates track the format", () =>
        {
            var mp3 = new AudioExportSettings { Format = AudioExportFormat.Mp3, Bitrate = 128 };
            Assert.Equal(160_000L, mp3.EstimateBytes(10), "128 kbps for 10 s");

            var wav = new AudioExportSettings
            {
                Format = AudioExportFormat.Wav, SampleRate = 44100, Channels = 2
            };
            Assert.Equal(1_764_000L, wav.EstimateBytes(10), "CD-rate stereo PCM for 10 s");

            wav.BitDepth = WavBitDepth.Pcm24;
            Assert.Equal(2_646_000L, wav.EstimateBytes(10), "24-bit is half again as big");
            wav.BitDepth = WavBitDepth.Float32;
            Assert.Equal(3_528_000L, wav.EstimateBytes(10), "32-bit float is twice 16-bit");
        });

        // ---------- End-to-end ----------

        RegisterFfmpegRoundTrip();
    }

    private static string Graph(
        SoundEditSession session, EffectCatalog catalog, AudioChainOptions options) =>
        AudioClipPlanner.BuildFilterComplex(session, catalog, options);

    /// <summary>
    /// Cuts a generated tone down and encodes it to every offered format, so a
    /// filter-graph mistake fails here instead of in the app.
    /// </summary>
    private static void RegisterFfmpegRoundTrip()
    {
        var locator = new FFmpegLocator(Environment.CurrentDirectory);

        TestRunner.Add("Sound export: edit → encode round-trip through ffmpeg", () =>
        {
            if (!locator.IsAvailable)
            {
                Console.WriteLine("        (skipped — ffmpeg not found on this machine)");
                return;
            }

            var workDir = Path.Combine(Path.GetTempPath(), $"ve_snd_{Guid.NewGuid():N}");
            Directory.CreateDirectory(workDir);
            try { RoundTripAsync(locator, workDir).GetAwaiter().GetResult(); }
            finally { Directory.Delete(workDir, recursive: true); }
        });
    }

    private static async Task RoundTripAsync(FFmpegLocator locator, string workDir)
    {
        var source = Path.Combine(workDir, "tone.wav");
        var generate = await ProcessRunner.RunAsync(locator.FfmpegPath!, new[]
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=6",
            "-ac", "2", "-ar", "44100", source
        });
        Assert.True(generate.Success, "tone generated: " + generate.StandardError);

        // Cut 1–2 s out of the middle, fade the survivor, quieten it.
        var session = SoundEditSession.ForFile(source, "tone.wav", 6);
        Assert.True(session.RemoveRange(1, 2), "middle second removed");
        session.Segments[0].FadeIn = 0.2;
        session.Segments[^1].FadeOut = 0.3;
        session.Segments[^1].Gain = 0.6;
        session.Effects.Add(new EffectInstance { Type = "echo", Parameters = { ["delay"] = 120, ["decay"] = 0.3 } });

        var catalog = new EffectCatalog();
        var exporter = new AudioClipExportService(locator, catalog);
        var probe = new MediaProbe(locator);

        var cases = new (AudioExportFormat Format, AudioNormalizeMode Normalize)[]
        {
            (AudioExportFormat.Wav, AudioNormalizeMode.None),
            (AudioExportFormat.Mp3, AudioNormalizeMode.Peak),
            (AudioExportFormat.OggVorbis, AudioNormalizeMode.None),
            (AudioExportFormat.Opus, AudioNormalizeMode.Loudness),
            (AudioExportFormat.Flac, AudioNormalizeMode.None),
            (AudioExportFormat.M4aAac, AudioNormalizeMode.None)
        };

        foreach (var (format, normalize) in cases)
        {
            var output = Path.Combine(workDir, "edited" + format.Extension());
            var settings = new AudioExportSettings
            {
                OutputPath = output,
                Format = format,
                Normalize = normalize,
                SampleRate = 44100,
                Channels = format == AudioExportFormat.Opus ? 1 : 2,
                Bitrate = 128
            };

            var result = await exporter.ExportAsync(session, settings);
            Assert.True(result.Success, $"{format} export: {result.Error}");
            Assert.True(new FileInfo(output).Length > 0, $"{format} file has content");

            var info = await probe.ProbeAsync(output);
            Assert.True(info is { HasAudio: true }, $"{format} output carries audio");
            // 6 s source minus the 1 s cut; lossy containers pad by a frame or two.
            Assert.Close(5.0, info!.DurationSeconds!.Value, $"{format} duration", 0.35);
        }

        await AssertSilenceTrimKeepsTheAudioAsync(locator, exporter, probe, workDir);
        await AssertPreviewMatchesExportAsync(locator, catalog, exporter, source, workDir);
    }

    /// <summary>
    /// A silence trim must remove silence and nothing else. ffmpeg's
    /// silenceremove discards the non-silence it buffers in start_duration, so
    /// a non-zero value there quietly ate 50 ms off each end.
    /// </summary>
    private static async Task AssertSilenceTrimKeepsTheAudioAsync(
        FFmpegLocator locator, AudioClipExportService exporter, MediaProbe probe, string workDir)
    {
        // 1 s silence + 2 s tone + 1 s silence.
        var padded = Path.Combine(workDir, "padded.wav");
        var build = await ProcessRunner.RunAsync(locator.FfmpegPath!, new[]
        {
            "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo",
            "-f", "lavfi", "-i", "sine=frequency=1000:duration=2:sample_rate=44100",
            "-filter_complex",
            "[0:a]atrim=0:1,aformat=channel_layouts=stereo[lead];" +
            "[1:a]aformat=channel_layouts=stereo[tone];" +
            "[0:a]atrim=0:1,aformat=channel_layouts=stereo[tail];" +
            "[lead][tone][tail]concat=n=3:v=0:a=1[out]",
            "-map", "[out]", padded
        });
        Assert.True(build.Success, "padded tone generated: " + build.StandardError);

        var trimmed = Path.Combine(workDir, "trimmed.wav");
        var result = await exporter.ExportAsync(
            SoundEditSession.ForFile(padded, "padded.wav", 4),
            new AudioExportSettings
            {
                OutputPath = trimmed,
                Format = AudioExportFormat.Wav,
                SampleRate = 44100,
                TrimSilence = true
            });
        Assert.True(result.Success, "silence-trimmed export: " + result.Error);

        var info = await probe.ProbeAsync(trimmed);
        // 2 s of tone, plus the 50 ms of edge silence the trim deliberately keeps
        // on each side. Anything under 2 s means real audio was thrown away.
        Assert.Close(2.1, info!.DurationSeconds!.Value, "only the silence was trimmed", 0.06);
    }

    /// <summary>
    /// The audition must be the export, windowed — not a second rendering path.
    /// Proven by summing the two with one phase-inverted: identical audio nulls
    /// out, and anything left over is the difference the user would hear.
    /// Normalization is excluded on purpose (see AudioChainOptions.ForPreview).
    /// </summary>
    private static async Task AssertPreviewMatchesExportAsync(
        FFmpegLocator locator, EffectCatalog catalog, AudioClipExportService exporter,
        string source, string workDir)
    {
        // Fades, a master level and a silence trim: everything a naive
        // model-slicing preview used to drop.
        var session = SoundEditSession.ForFile(source, "tone.wav", 6);
        session.Segments[0].FadeIn = 2;
        session.Segments[0].FadeOut = 2;
        session.MasterGain = 0.8;

        var settings = new AudioExportSettings
        {
            OutputPath = Path.Combine(workDir, "full.wav"),
            Format = AudioExportFormat.Wav,
            SampleRate = 44100,
            Channels = 2,
            TrimSilence = true
        };
        Assert.True((await exporter.ExportAsync(session, settings)).Success, "reference export");

        // The audition of [1.5 s, 4.5 s) of that result…
        const double windowStart = 1.5;
        const double windowLength = 3.0;
        var preview = Path.Combine(workDir, "preview.wav");
        var previewRun = await ProcessRunner.RunAsync(locator.FfmpegPath!,
            AudioClipPlanner.BuildPreviewArguments(
                session, catalog, settings, preview, windowStart, windowLength));
        Assert.True(previewRun.Success, "preview render: " + previewRun.StandardError);

        // …against the same window cut straight out of the exported file.
        var reference = Path.Combine(workDir, "window.wav");
        var cut = await ProcessRunner.RunAsync(locator.FfmpegPath!, new[]
        {
            "-y", "-loglevel", "error",
            "-i", settings.OutputPath,
            "-af", $"atrim=start={windowStart}:end={windowStart + windowLength},asetpts=N/SR/TB",
            reference
        });
        Assert.True(cut.Success, "reference window: " + cut.StandardError);

        var nulled = await ProcessRunner.RunAsync(locator.FfmpegPath!, new[]
        {
            "-loglevel", "info",
            "-i", preview,
            "-i", reference,
            "-filter_complex",
            "[1:a]volume=-1[inverted];[0:a][inverted]amix=inputs=2:normalize=0,volumedetect",
            "-f", "null", "-"
        });
        var residual = AudioClipPlanner.ParseMaxVolumeDb(nulled.StandardError);
        Assert.True(residual is not null, "the null test produced a level reading");
        Assert.True(residual!.Value < -60,
            $"audition and export cancel out (residual {residual.Value:0.#} dBFS, want < -60)");
    }
}
