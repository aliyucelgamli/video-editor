using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>Changes one parameter value of an applied effect.</summary>
public class SetEffectParameterCommand : IEditorCommand
{
    private readonly EffectInstance _effect;
    private readonly string _key;
    private readonly double _newValue;
    private readonly double _oldValue;
    private readonly bool _hadValue;

    public SetEffectParameterCommand(EffectInstance effect, string key, double newValue)
    {
        _effect = effect;
        _key = key;
        _newValue = newValue;
        _hadValue = effect.Parameters.TryGetValue(key, out _oldValue);
    }

    public string Description => $"Change {_key}";

    public void Execute() => _effect.Parameters[_key] = _newValue;

    public void Undo()
    {
        if (_hadValue) _effect.Parameters[_key] = _oldValue;
        else _effect.Parameters.Remove(_key);
    }
}
