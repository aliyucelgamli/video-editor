using System.Globalization;

namespace VideoEditor.App.Ui;

/// <summary>Shared human-readable time formatting for status bars and windows.</summary>
public static class TimeText
{
    /// <summary>"m:ss.f", growing to "h:mm:ss.f" past an hour.</summary>
    public static string Compact(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
    }

    /// <summary>"m:ss", growing to "h:mm:ss" past an hour (durations, ETAs).</summary>
    public static string Span(TimeSpan span) => span.TotalHours >= 1
        ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
}
