using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>Removes an effect from a chain, remembering its position for undo.</summary>
public class RemoveEffectCommand : IEditorCommand
{
    private readonly IList<EffectInstance> _chain;
    private readonly EffectInstance _effect;
    private int _index;

    public RemoveEffectCommand(IList<EffectInstance> chain, EffectInstance effect, string effectName)
    {
        _chain = chain;
        _effect = effect;
        Description = $"Remove {effectName}";
    }

    public string Description { get; }

    public void Execute()
    {
        _index = _chain.IndexOf(_effect);
        if (_index >= 0) _chain.RemoveAt(_index);
    }

    public void Undo()
    {
        if (_index < 0) return;
        _chain.Insert(Math.Min(_index, _chain.Count), _effect);
    }
}
