using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>Moves an effect to another position in its chain.</summary>
public class ReorderEffectCommand : IEditorCommand
{
    private readonly IList<EffectInstance> _chain;
    private readonly int _fromIndex;
    private readonly int _toIndex;

    public ReorderEffectCommand(IList<EffectInstance> chain, int fromIndex, int toIndex)
    {
        _chain = chain;
        _fromIndex = fromIndex;
        _toIndex = toIndex;
    }

    public string Description => "Reorder effects";

    public void Execute() => Move(_fromIndex, _toIndex);

    public void Undo() => Move(_toIndex, _fromIndex);

    private void Move(int from, int to)
    {
        if (from < 0 || from >= _chain.Count || to < 0 || to >= _chain.Count || from == to) return;
        var effect = _chain[from];
        _chain.RemoveAt(from);
        _chain.Insert(to, effect);
    }
}
