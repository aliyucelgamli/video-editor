using System.IO;
using System.Media;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;
using VideoEditor.MediaEngine;
using VideoEditor.MediaEngine.Export;
using VideoEditor.MediaEngine.Ffmpeg;

namespace VideoEditor.App.Services;

/// <summary>
/// Audio for preview playback — NuGet-free: FFmpeg mixes the timeline from the
/// playhead onward into a temporary WAV (same <see cref="AudioMixPlanner"/> as
/// export, so effects, volume, mute/solo all apply), then
/// <see cref="SoundPlayer"/> plays it while the video clock runs.
/// </summary>
public class PreviewAudioService : IDisposable
{
    /// <summary>Mixing very long tails costs startup latency; cap the preview mix.</summary>
    private const double MaxMixSeconds = 300;

    private readonly FFmpegLocator _locator;
    private readonly CachePaths _cache;
    private readonly IEffectCatalog _catalog;
    private readonly SoundPlayer _player = new();
    private string? _currentWav;
    private long _generation;

    public PreviewAudioService(FFmpegLocator locator, CachePaths cache, IEffectCatalog catalog)
    {
        _locator = locator;
        _cache = cache;
        _catalog = catalog;
    }

    /// <summary>
    /// Mixes and starts audio from <paramref name="fromTime"/>. Returns false
    /// (video plays silently) when there is nothing to play or mixing fails.
    /// A newer Start/Stop call cancels the effect of an older one.
    /// </summary>
    public async Task<bool> StartAsync(Project project, double fromTime, CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        StopPlayer();

        if (_locator.FfmpegPath is null) return false;

        var end = Math.Min(project.Duration, fromTime + MaxMixSeconds);
        if (end - fromTime <= 0.05) return false;

        var range = new TimeRange { Start = fromTime, End = end };
        var wavPath = Path.Combine(_cache.Preview, $"preview_audio_{Guid.NewGuid():N}.wav");

        try
        {
            Directory.CreateDirectory(_cache.Preview);
            var arguments = AudioMixPlanner.BuildMixArguments(
                project, _catalog, range, project.Settings.AudioSampleRate, wavPath);
            var result = await ProcessRunner.RunAsync(_locator.FfmpegPath, arguments, cancellationToken)
                .ConfigureAwait(true); // stay on the UI thread for SoundPlayer

            if (!result.Success || generation != _generation)
            {
                TryDelete(wavPath);
                return false;
            }

            CleanupPrevious();
            _currentWav = wavPath;
            _player.SoundLocation = wavPath;
            _player.Load();
            _player.Play();
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(wavPath);
            return false;
        }
        catch
        {
            TryDelete(wavPath);
            return false; // audio is best-effort; video preview must keep working
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
        if (_currentWav is { } previous)
        {
            _currentWav = null;
            TryDelete(previous);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp cleanup is best effort */ }
    }
}
