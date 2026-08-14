namespace VideoEditor.Domain.Effects;

/// <summary>
/// Lookup for every effect the application knows about
/// (built-ins plus user-imported .vefx effects).
/// </summary>
public interface IEffectCatalog
{
    IReadOnlyList<EffectDefinition> All { get; }

    EffectDefinition? Find(string id);
}
