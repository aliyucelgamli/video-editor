namespace VideoEditor.Domain;

/// <summary>Project-wide output and timeline settings.</summary>
public class ProjectSettings
{
    public string Name { get; set; } = "Untitled Project";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public double FrameRate { get; set; } = 30.0;
    public int AudioSampleRate { get; set; } = 48000;
}
