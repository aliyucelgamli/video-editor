using System.Text.Json;
using System.Text.Json.Serialization;
using VideoEditor.Application.Services;
using VideoEditor.Domain;

namespace VideoEditor.ProjectIO;

/// <summary>
/// Human-readable JSON project format (.veproj).
/// Stores media references and edit state only — never media content.
/// </summary>
public class JsonProjectSerializer : IProjectSerializer
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string DefaultExtension => ".veproj";

    public void Save(Project project, string path)
    {
        var file = new ProjectFile { FormatVersion = CurrentFormatVersion, Project = project };
        var json = JsonSerializer.Serialize(file, Options);

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write to a temp file first so a crash mid-save cannot corrupt the project.
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    public Project Load(string path)
    {
        var json = File.ReadAllText(path);

        ProjectFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ProjectFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException("The project file is not valid JSON.", ex);
        }

        if (file?.Project is null)
            throw new ProjectFormatException("The project file contains no project data.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new ProjectFormatException(
                $"Project format v{file.FormatVersion} is newer than this application supports (v{CurrentFormatVersion}).");

        return file.Project;
    }
}
