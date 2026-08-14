namespace VideoEditor.ProjectIO;

/// <summary>Thrown when a project file cannot be read or has an unsupported format.</summary>
public class ProjectFormatException : Exception
{
    public ProjectFormatException(string message) : base(message) { }
    public ProjectFormatException(string message, Exception inner) : base(message, inner) { }
}
