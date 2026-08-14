using System.Security.Cryptography;
using System.Text;

namespace VideoEditor.MediaEngine;

/// <summary>
/// Locations of regenerable cached artifacts (thumbnails, waveforms, previews).
/// Deleting the cache never breaks a project — everything is rebuilt on demand.
/// </summary>
public class CachePaths
{
    public CachePaths(string root)
    {
        Root = root;
        Thumbnails = Path.Combine(root, "thumbnails");
        Waveform = Path.Combine(root, "waveform");
        Preview = Path.Combine(root, "preview");
        Proxy = Path.Combine(root, "proxy");
    }

    public string Root { get; }
    public string Thumbnails { get; }
    public string Waveform { get; }
    public string Preview { get; }
    public string Proxy { get; }

    /// <summary>
    /// Finds the repository/app root (the folder containing "cache" and "user")
    /// by walking up from the given start directory; falls back to a local cache dir.
    /// </summary>
    public static CachePaths Locate(string? startDirectory = null)
    {
        var current = new DirectoryInfo(startDirectory ?? Environment.CurrentDirectory);
        for (var depth = 0; current != null && depth < 8; depth++, current = current.Parent)
        {
            var candidate = Path.Combine(current.FullName, "cache");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(current.FullName, "user")))
                return new CachePaths(candidate);
        }
        return new CachePaths(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoEditor", "cache"));
    }

    /// <summary>Same walk as <see cref="Locate"/> but returns the app root folder itself.</summary>
    public static string LocateAppRoot(string? startDirectory = null)
    {
        var current = new DirectoryInfo(startDirectory ?? Environment.CurrentDirectory);
        for (var depth = 0; current != null && depth < 8; depth++, current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "cache")) &&
                Directory.Exists(Path.Combine(current.FullName, "user")))
                return current.FullName;
        }
        return Environment.CurrentDirectory;
    }

    /// <summary>
    /// Builds a stable cache key from a source file plus arbitrary variant data
    /// (size, time offset, …). Includes the file's write time so cache entries
    /// invalidate automatically when the source changes.
    /// </summary>
    public static string KeyFor(string sourcePath, params object[] variantParts)
    {
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(sourcePath).Ticks; } catch { /* missing file → stable key */ }

        var builder = new StringBuilder(sourcePath.ToLowerInvariant()).Append('|').Append(stamp);
        foreach (var part in variantParts)
            builder.Append('|').Append(Convert.ToString(part, System.Globalization.CultureInfo.InvariantCulture));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }
}
