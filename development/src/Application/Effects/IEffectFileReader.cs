using VideoEditor.Domain.Effects;

namespace VideoEditor.Application.Effects;

/// <summary>Reads an effect definition from a file (.vefx). Implemented in ProjectIO.</summary>
public interface IEffectFileReader
{
    string DefaultExtension { get; }

    EffectDefinition Load(string path);
}
