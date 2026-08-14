using VideoEditor.Domain;

namespace VideoEditor.ProjectIO;

/// <summary>On-disk wrapper: format version + project payload (backward compatibility).</summary>
public class ProjectFile
{
    public int FormatVersion { get; set; } = JsonProjectSerializer.CurrentFormatVersion;
    public string Generator { get; set; } = "VideoEditor";
    public Project? Project { get; set; }
}
