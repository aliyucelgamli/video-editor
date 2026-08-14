using VideoEditor.Domain;

namespace VideoEditor.MediaEngine.Export;

/// <summary>Output container/codec choices offered by the export dialog.</summary>
public enum ExportFormat
{
    Mp4H264,
    Mp4Hevc,
    WebMVp9,
    Mp3,
    Wav
}

public static class ExportFormats
{
    public static bool IsAudioOnly(this ExportFormat format) =>
        format is ExportFormat.Mp3 or ExportFormat.Wav;

    public static string Extension(this ExportFormat format) => format switch
    {
        ExportFormat.Mp4H264 or ExportFormat.Mp4Hevc => ".mp4",
        ExportFormat.WebMVp9 => ".webm",
        ExportFormat.Mp3 => ".mp3",
        _ => ".wav"
    };

    public static string DisplayName(this ExportFormat format) => format switch
    {
        ExportFormat.Mp4H264 => "MP4 — H.264 + AAC",
        ExportFormat.Mp4Hevc => "MP4 — H.265/HEVC + AAC",
        ExportFormat.WebMVp9 => "WebM — VP9 + Opus",
        ExportFormat.Mp3 => "MP3 — audio only",
        _ => "WAV — audio only (lossless)"
    };

    public static string SaveDialogFilter(this ExportFormat format) => format switch
    {
        ExportFormat.Mp4H264 or ExportFormat.Mp4Hevc => "MP4 Video (*.mp4)|*.mp4",
        ExportFormat.WebMVp9 => "WebM Video (*.webm)|*.webm",
        ExportFormat.Mp3 => "MP3 Audio (*.mp3)|*.mp3",
        _ => "WAV Audio (*.wav)|*.wav"
    };
}

/// <summary>Options for one export run.</summary>
public class ExportSettings
{
    public string OutputPath { get; set; } = string.Empty;
    public ExportFormat Format { get; set; } = ExportFormat.Mp4H264;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FrameRate { get; set; } = 30;
    public int AudioSampleRate { get; set; } = 48000;

    /// <summary>Constant Rate Factor (lower = better quality, 18–28 sensible).</summary>
    public int Crf { get; set; } = 20;

    /// <summary>
    /// Timeline span to export. Null = whole project.
    /// The UI passes the yellow start/end region here when one is set.
    /// </summary>
    public TimeRange? Range { get; set; }

    public static ExportSettings FromProject(Project project, string outputPath) => new()
    {
        OutputPath = outputPath,
        Width = project.Settings.Width,
        Height = project.Settings.Height,
        FrameRate = project.Settings.FrameRate,
        AudioSampleRate = project.Settings.AudioSampleRate,
        Range = project.ExportRange?.Normalized()
    };
}
