using VideoEditor.Domain;

namespace VideoEditor.Application.Commands;

/// <summary>Toggles an applied effect on/off without removing it from the chain.</summary>
public class SetEffectEnabledCommand : IEditorCommand
{
    private readonly EffectInstance _effect;
    private readonly bool _newValue;
    private readonly bool _oldValue;

    public SetEffectEnabledCommand(EffectInstance effect, bool enabled)
    {
        _effect = effect;
        _newValue = enabled;
        _oldValue = effect.Enabled;
    }

    public string Description => _newValue ? "Enable effect" : "Disable effect";

    public void Execute() => _effect.Enabled = _newValue;

    public void Undo() => _effect.Enabled = _oldValue;
}
