using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>Appends an effect to an effect chain (event, track or output level).</summary>
public class AddEffectCommand : IEditorCommand
{
    private readonly IList<EffectInstance> _chain;
    private readonly EffectInstance _effect;

    public AddEffectCommand(IList<EffectInstance> chain, EffectInstance effect, string effectName, string ownerName)
    {
        _chain = chain;
        _effect = effect;
        Description = $"Add {effectName} to '{ownerName}'";
    }

    public string Description { get; }

    public void Execute() => _chain.Add(_effect);

    public void Undo() => _chain.Remove(_effect);
}
