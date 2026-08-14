using VideoEditor.Domain;

namespace VideoEditor.Application.Services;

/// <summary>Persists and restores the project model. Implemented in the ProjectIO module.</summary>
public interface IProjectSerializer
{
    string DefaultExtension { get; }
    void Save(Project project, string path);
    Project Load(string path);
}
