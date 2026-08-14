using System.Runtime.InteropServices;

namespace VideoEditor.MediaEngine.Ffmpeg;

/// <summary>
/// Finds the ffmpeg / ffprobe executables. Search order:
/// 1) VIDEOEDITOR_FFMPEG_DIR environment variable,
/// 2) a "tools/ffmpeg" folder next to the app root,
/// 3) the system PATH,
/// 4) common install locations.
/// All media features degrade gracefully when FFmpeg is missing.
/// </summary>
public class FFmpegLocator
{
    private readonly string _appRoot;
    private string? _ffmpeg;
    private string? _ffprobe;
    private bool _searched;

    public FFmpegLocator(string appRoot) => _appRoot = appRoot;

    public string? FfmpegPath { get { EnsureSearched(); return _ffmpeg; } }
    public string? FfprobePath { get { EnsureSearched(); return _ffprobe; } }
    public bool IsAvailable => FfmpegPath != null;

    /// <summary>Download page shown to the user when FFmpeg cannot be found.</summary>
    public const string DownloadUrl = "https://www.gyan.dev/ffmpeg/builds/";

    private void EnsureSearched()
    {
        if (_searched) return;
        _searched = true;
        _ffmpeg = FindExecutable("ffmpeg");
        _ffprobe = FindExecutable("ffprobe");
    }

    private string? FindExecutable(string name)
    {
        var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? name + ".exe" : name;

        foreach (var directory in CandidateDirectories())
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private IEnumerable<string> CandidateDirectories()
    {
        if (Environment.GetEnvironmentVariable("VIDEOEDITOR_FFMPEG_DIR") is { Length: > 0 } custom)
            yield return custom;

        yield return Path.Combine(_appRoot, "tools", "ffmpeg");
        yield return Path.Combine(_appRoot, "tools", "ffmpeg", "bin");

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return entry.Trim();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return @"C:\ffmpeg\bin";
            yield return @"C:\Program Files\ffmpeg\bin";
        }
        else
        {
            yield return "/usr/bin";
            yield return "/usr/local/bin";
        }
    }
}
