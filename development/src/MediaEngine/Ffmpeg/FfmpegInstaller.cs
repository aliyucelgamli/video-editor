using System.IO.Compression;
using System.Net.Http;

namespace VideoEditor.MediaEngine.Ffmpeg;

/// <summary>
/// Downloads a portable FFmpeg build and drops ffmpeg.exe + ffprobe.exe into
/// the app's tools/ffmpeg folder — the one-click fix for the "FFmpeg not
/// found" state. BCL only (HttpClient + ZipArchive), no packages.
/// </summary>
public class FfmpegInstaller
{
    /// <summary>Stable "latest release, essentials" build (~90 MB zip).</summary>
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <summary>Fallback mirror when the primary host is unreachable.</summary>
    public const string FallbackUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly string[] WantedFiles = { "ffmpeg.exe", "ffprobe.exe" };

    /// <summary>
    /// Downloads and installs FFmpeg into <paramref name="targetDirectory"/>.
    /// Progress: 0–0.9 download, 0.9–1.0 extraction. Throws with a friendly
    /// message when both hosts fail.
    /// </summary>
    public async Task InstallAsync(
        string targetDirectory, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var zipPath = Path.Combine(Path.GetTempPath(), $"ffmpeg_download_{Guid.NewGuid():N}.zip");
        try
        {
            try
            {
                await DownloadAsync(DownloadUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                       !cancellationToken.IsCancellationRequested)
            {
                await DownloadAsync(FallbackUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);
            }

            ExtractPortableBuild(zipPath, targetDirectory);
            progress?.Report(1.0);
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* temp cleanup */ }
        }
    }

    private static async Task DownloadAsync(
        string url, string zipPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(zipPath);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;
            if (totalBytes is { } total and > 0)
                progress?.Report(0.9 * readTotal / total);
        }
    }

    /// <summary>
    /// Pulls ffmpeg.exe / ffprobe.exe out of a portable build zip (any layout —
    /// entries are matched by file name). Exposed for tests.
    /// </summary>
    public static void ExtractPortableBuild(string zipPath, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        using var archive = ZipFile.OpenRead(zipPath);

        var extracted = 0;
        foreach (var wanted in WantedFiles)
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(Path.GetFileName(e.FullName), wanted, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;

            entry.ExtractToFile(Path.Combine(targetDirectory, wanted), overwrite: true);
            extracted++;
        }

        if (extracted == 0)
            throw new InvalidOperationException(
                "The downloaded archive does not contain ffmpeg.exe — the build layout may have changed.");
    }
}
