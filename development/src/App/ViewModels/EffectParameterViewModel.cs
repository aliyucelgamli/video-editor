using System.Globalization;
using VideoEditor.App.Mvvm;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// A slider row for one effect parameter. Dragging updates the model live
/// (for immediate preview); one undoable command is issued when the drag ends.
/// </summary>
public class EffectParameterViewModel : ObservableObject
{
    private readonly EffectInstance _instance;
    private readonly EffectParameterDefinition _definition;
    private readonly Action<EffectInstance, EffectParameterDefinition, double, double> _onCommitted;
    private readonly Action _onPreview;
    private double _editStartValue;
    private bool _isEditing;

    public EffectParameterViewModel(
        EffectInstance instance,
        EffectParameterDefinition definition,
        Action<EffectInstance, EffectParameterDefinition, double, double> onCommitted,
        Action onPreview)
    {
        _instance = instance;
        _definition = definition;
        _onCommitted = onCommitted;
        _onPreview = onPreview;
    }

    public string Label => _definition.Label;
    public double Min => _definition.Min;
    public double Max => _definition.Max;

    public double Value
    {
        get => _instance.Parameters.TryGetValue(_definition.Key, out var v) ? v : _definition.Default;
        set
        {
            var clamped = _definition.Clamp(value);
            if (Math.Abs(Value - clamped) < 1e-9) return;
            _instance.Parameters[_definition.Key] = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueLabel));
            _onPreview();
        }
    }

    public string ValueLabel
    {
        get
        {
            var text = Value.ToString(Math.Abs(Max - Min) > 20 ? "0" : "0.##", CultureInfo.InvariantCulture);
            return _definition.Unit is { Length: > 0 } unit ? $"{text} {unit}" : text;
        }
    }

    /// <summary>Called when the user grabs the slider (stores the undo baseline).</summary>
    public void BeginEdit()
    {
        if (_isEditing) return;
        _isEditing = true;
        _editStartValue = Value;
    }

    /// <summary>Called when the user releases the slider (issues one undoable command).</summary>
    public void EndEdit()
    {
        if (!_isEditing) return;
        _isEditing = false;
        if (Math.Abs(_editStartValue - Value) > 1e-9)
            _onCommitted(_instance, _definition, _editStartValue, Value);
    }
}
