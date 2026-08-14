using VideoEditor.Domain;

namespace VideoEditor.MediaEngine.Export;

/// <summary>Options for one export run.</summary>
public class ExportSettings
{
    public string OutputPath { get; set; } = string.Empty;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FrameRate { get; set; } = 30;
    public int AudioSampleRate { get; set; } = 48000;

    /// <summary>Constant Rate Factor for H.264 (lower = better quality, 18–28 sensible).</summary>
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
