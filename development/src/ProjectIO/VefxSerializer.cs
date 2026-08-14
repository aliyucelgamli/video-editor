using System.Text.Json;
using System.Text.Json.Serialization;
using VideoEditor.Application.Effects;
using VideoEditor.Domain.Effects;

namespace VideoEditor.ProjectIO;

/// <summary>
/// Reads and writes .vefx effect files: a small JSON format describing an
/// effect (name, targets, parameters and its kernel pipeline). Anyone can
/// author one by hand and drop it into user/effects.
/// </summary>
public class VefxSerializer : IEffectFileReader
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string DefaultExtension => ".vefx";

    public EffectDefinition Load(string path) => Parse(File.ReadAllText(path));

    public void Save(EffectDefinition definition, string path)
    {
        var file = new VefxFile { FormatVersion = CurrentFormatVersion, Effect = definition };
        File.WriteAllText(path, JsonSerializer.Serialize(file, Options));
    }

    /// <summary>Parses and validates .vefx JSON content (exposed for tests).</summary>
    public static EffectDefinition Parse(string json)
    {
        VefxFile? file;
        try
        {
            file = JsonSerializer.Deserialize<VefxFile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProjectFormatException("The .vefx file is not valid JSON.", ex);
        }

        if (file?.Effect is null)
            throw new ProjectFormatException("The .vefx file contains no effect definition.");
        if (file.FormatVersion > CurrentFormatVersion)
            throw new ProjectFormatException(
                $".vefx format v{file.FormatVersion} is newer than this application supports (v{CurrentFormatVersion}).");

        Validate(file.Effect);
        return file.Effect;
    }

    private static void Validate(EffectDefinition effect)
    {
        if (string.IsNullOrWhiteSpace(effect.Id))
            throw new ProjectFormatException("The effect has no id.");
        if (string.IsNullOrWhiteSpace(effect.Name))
            throw new ProjectFormatException("The effect has no name.");
        if (effect.Targets == EffectTarget.None)
            throw new ProjectFormatException("The effect declares no targets (video/audio/image).");
        if (effect.Steps.Count == 0)
            throw new ProjectFormatException("The effect has no processing steps.");
        if (effect.Steps.Any(s => string.IsNullOrWhiteSpace(s.Kernel)))
            throw new ProjectFormatException("Every effect step needs a kernel name.");
    }

    private class VefxFile
    {
        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public EffectDefinition? Effect { get; set; }
    }
}
