using VideoEditor.Domain.Effects;

namespace VideoEditor.Application.Effects;

/// <summary>
/// Registry of all known effects: built-ins plus user effects imported
/// from .vefx files. User effects with an existing id override built-ins,
/// which lets users tweak a stock effect by shipping their own version.
/// </summary>
public class EffectCatalog : IEffectCatalog
{
    private readonly List<EffectDefinition> _builtIns;
    private readonly Dictionary<string, EffectDefinition> _userEffects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when user effects are registered or cleared.</summary>
    public event EventHandler? Changed;

    public EffectCatalog(IEnumerable<EffectDefinition>? builtIns = null)
    {
        _builtIns = (builtIns ?? BuiltInEffects.CreateAll()).ToList();
    }

    public IReadOnlyList<EffectDefinition> All =>
        _builtIns.Where(b => !_userEffects.ContainsKey(b.Id))
                 .Concat(_userEffects.Values)
                 .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                 .ToList();

    public EffectDefinition? Find(string id) =>
        _userEffects.TryGetValue(id, out var user)
            ? user
            : _builtIns.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public void RegisterUserEffect(EffectDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id))
            throw new ArgumentException("An effect definition needs a non-empty id.", nameof(definition));
        definition.IsBuiltIn = false;
        _userEffects[definition.Id] = definition;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearUserEffects()
    {
        if (_userEffects.Count == 0) return;
        _userEffects.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
