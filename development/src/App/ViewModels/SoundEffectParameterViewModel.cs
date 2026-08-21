using VideoEditor.App.Mvvm;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// One slider of an effect in the sound editor's chain. Writes straight into the
/// instance's parameter map, which is all the ffmpeg planner reads — no undo
/// command, because a sound-editor session is not part of the project model.
/// </summary>
public sealed class SoundEffectParameterViewModel : ObservableObject
{
    private readonly EffectParameterDefinition _definition;
    private readonly EffectInstance _instance;
    private readonly Action _edited;

    public SoundEffectParameterViewModel(
        EffectParameterDefinition definition, EffectInstance instance, Action edited)
    {
        _definition = definition;
        _instance = instance;
        _edited = edited;
    }

    public string Label => _definition.Label;
    public double Minimum => _definition.Min;
    public double Maximum => _definition.Max;

    /// <summary>Slider granularity: 100 steps across the range, never coarser than 0.01.</summary>
    public double Step => Math.Max(0.01, (_definition.Max - _definition.Min) / 100.0);

    public double Value
    {
        get => _definition.Clamp(
            _instance.Parameters.TryGetValue(_definition.Key, out var value) ? value : _definition.Default);
        set
        {
            var clamped = _definition.Clamp(value);
            if (Math.Abs(Value - clamped) < 1e-6) return;
            _instance.Parameters[_definition.Key] = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueLabel));
            _edited();
        }
    }

    public string ValueLabel => string.IsNullOrEmpty(_definition.Unit)
        ? $"{Value:0.##}"
        : $"{Value:0.##} {_definition.Unit}";

    /// <summary>Back to the effect author's default.</summary>
    public void Reset() => Value = _definition.Default;
}
