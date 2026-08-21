using System.IO;
using System.Media;
using VideoEditor.Domain.Effects;
using VideoEditor.Domain.Sound;
using VideoEditor.MediaEngine;
using VideoEditor.MediaEngine.Audio;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.App.Services;

/// <summary>
/// Audition for the sound editor. Same NuGet-free approach as
/// <see cref="PreviewAudioService"/>: FFmpeg renders the edited result from the
/// playhead into a temporary WAV through the very chain the export uses — the
/// audition window is cut out of the finished audio, not out of the model — then
/// <see cref="SoundPlayer"/> plays it. Hearing the edit therefore proves the
/// export, not a separate preview path.
/// </summary>
public sealed class SoundPreviewService : IDisposable
{
    /// <summary>Rendering a long tail costs start-up latency; cap the audition.</summary>
    public const double MaxPreviewSeconds = 120;

    private readonly FFmpegLocator _locator;
    private readonly CachePaths _cache;
    private readonly IEffectCatalog _catalog;
    private readonly SoundPlayer _player = new();
    private string? _currentWav;
    private long _generation;

    public SoundPreviewService(FFmpegLocator locator, CachePaths cache, IEffectCatalog catalog)
    {
        _locator = locator;
        _cache = cache;
        _catalog = catalog;
    }

    /// <summary>
    /// Renders and starts playing from <paramref name="fromOutputTime"/>.
    /// Returns the length actually queued, or 0 when there was nothing to play
    /// (no ffmpeg, empty clip, a newer call took over, or the render failed).
    /// </summary>
    public async Task<double> StartAsync(
        SoundEditSession session, AudioExportSettings settings, double fromOutputTime,
        CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        StopPlayer();

        if (_locator.FfmpegPath is null || session.IsEmpty) return 0;

        var remaining = session.OutputDuration - fromOutputTime;
        if (remaining <= 0.05) return 0;
        var length = Math.Min(remaining, MaxPreviewSeconds);

        var wavPath = Path.Combine(_cache.Preview, $"sound_edit_{Guid.NewGuid():N}.wav");
        try
        {
            Directory.CreateDirectory(_cache.Preview);
            var arguments = AudioClipPlanner.BuildPreviewArguments(
                session, _catalog, settings, wavPath, fromOutputTime, length);

            var result = await ProcessRunner.RunAsync(_locator.FfmpegPath, arguments, cancellationToken)
                .ConfigureAwait(true); // stay on the UI thread for SoundPlayer

            if (!result.Success || generation != _generation)
            {
                TryDelete(wavPath);
                return 0;
            }

            CleanupPrevious();
            _currentWav = wavPath;
            _player.SoundLocation = wavPath;
            _player.Load();
            _player.Play();

            // The render can come out shorter than the window asked for — a
            // silence trim eats some — and the caller's playhead clock has to
            // run for exactly as long as the sound does.
            return WavDurationSeconds(wavPath, length);
        }
        catch (OperationCanceledException)
        {
            TryDelete(wavPath);
            return 0;
        }
        catch
        {
            TryDelete(wavPath);
            return 0; // auditioning is best-effort; the editor must stay usable
        }
    }

    public void Stop()
    {
        _generation++;
        StopPlayer();
        CleanupPrevious();
    }

    public void Dispose()
    {
        Stop();
        _player.Dispose();
    }

    private void StopPlayer()
    {
        try { _player.Stop(); } catch { /* never break playback control */ }
    }

    private void CleanupPrevious()
    {
        if (_currentWav is not { } previous) return;
        _currentWav = null;
        TryDelete(previous);
    }

    /// <summary>
    /// Playing time of a PCM WAV, read from its header (data bytes ÷ byte rate).
    /// Walks the RIFF chunks rather than assuming a 44-byte header, and falls
    /// back to the requested length if anything looks unfamiliar.
    /// </summary>
    private static double WavDurationSeconds(string path, double fallback)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (new string(reader.ReadChars(4)) != "RIFF") return fallback;
            reader.ReadUInt32(); // riff size
            if (new string(reader.ReadChars(4)) != "WAVE") return fallback;

            var byteRate = 0u;
            while (stream.Position + 8 <= stream.Length)
            {
                var id = new string(reader.ReadChars(4));
                var size = reader.ReadUInt32();
                var next = stream.Position + size + (size % 2); // chunks are word-aligned

                if (id == "fmt " && size >= 16)
                {
                    reader.ReadUInt16(); // format tag
                    reader.ReadUInt16(); // channels
                    reader.ReadUInt32(); // sample rate
                    byteRate = reader.ReadUInt32();
                }
                else if (id == "data")
                {
                    if (byteRate == 0) return fallback;
                    // The window bounds the render, so a longer reading means an
                    // unpatched header (a killed writer) — never a longer sound.
                    return Math.Clamp(size / (double)byteRate, 0, fallback);
                }

                if (next > stream.Length) break;
                stream.Position = next;
            }
        }
        catch
        {
            // A malformed header is not worth failing playback over.
        }
        return fallback;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp cleanup is best effort */ }
    }
}
