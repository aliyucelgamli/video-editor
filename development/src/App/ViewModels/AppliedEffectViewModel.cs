using System.Collections.ObjectModel;
using VideoEditor.App.Mvvm;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;

namespace VideoEditor.App.ViewModels;

/// <summary>An effect applied to the selected event, with editable parameters.</summary>
public class AppliedEffectViewModel : ObservableObject
{
    private readonly EffectInstance _instance;
    private readonly Action<AppliedEffectViewModel> _onRemove;
    private readonly Action<EffectInstance, bool> _onSetEnabled;

    public AppliedEffectViewModel(
        EffectInstance instance,
        EffectDefinition? definition,
        Action<AppliedEffectViewModel> onRemove,
        Action<EffectInstance, bool> onSetEnabled,
        Action<EffectInstance, EffectParameterDefinition, double, double> onParameterCommitted,
        Action onParameterPreview)
    {
        _instance = instance;
        _onRemove = onRemove;
        _onSetEnabled = onSetEnabled;

        Name = definition?.Name ?? instance.Type;
        IsMissing = definition is null;
        RemoveCommand = new RelayCommand(() => _onRemove(this));

        if (definition != null)
        {
            foreach (var parameter in definition.Parameters)
                Parameters.Add(new EffectParameterViewModel(
                    instance, parameter, onParameterCommitted, onParameterPreview));
        }
    }

    public EffectInstance Instance => _instance;
    public string Name { get; }
    public bool IsMissing { get; }
    public RelayCommand RemoveCommand { get; }
    public ObservableCollection<EffectParameterViewModel> Parameters { get; } = new();

    public bool IsEnabled
    {
        get => _instance.Enabled;
        set
        {
            if (_instance.Enabled == value) return;
            _onSetEnabled(_instance, value);
            OnPropertyChanged();
        }
    }
}
