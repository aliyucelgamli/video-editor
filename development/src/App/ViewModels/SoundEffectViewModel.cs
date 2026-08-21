using System.Collections.ObjectModel;
using VideoEditor.App.Mvvm;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;

namespace VideoEditor.App.ViewModels;

/// <summary>
/// One effect in the sound editor's master chain, with its sliders. The chain is
/// the same <see cref="EffectInstance"/> list the timeline uses, so an effect
/// authored in a .vefx file behaves identically in both places.
/// </summary>
public sealed class SoundEffectViewModel : ObservableObject
{
    private readonly EffectInstance _instance;
    private readonly Action _edited;

    public SoundEffectViewModel(EffectInstance instance, EffectDefinition? definition, Action edited)
    {
        _instance = instance;
        _edited = edited;
        Name = definition?.Name ?? instance.Type;
        Description = definition?.Description ?? string.Empty;
        IsKnown = definition != null;

        Parameters = new ObservableCollection<SoundEffectParameterViewModel>(
            (definition?.Parameters ?? new List<EffectParameterDefinition>())
            .Select(p => new SoundEffectParameterViewModel(p, instance, edited)));
    }

    public Guid Id => _instance.Id;
    public string Name { get; }
    public string Description { get; }

    /// <summary>False when the .vefx that defined this effect is no longer installed.</summary>
    public bool IsKnown { get; }

    public ObservableCollection<SoundEffectParameterViewModel> Parameters { get; }

    public bool HasParameters => Parameters.Count > 0;

    public bool Enabled
    {
        get => _instance.Enabled;
        set
        {
            if (_instance.Enabled == value) return;
            _instance.Enabled = value;
            OnPropertyChanged();
            _edited();
        }
    }

    public string ToolTip => IsKnown
        ? string.IsNullOrEmpty(Description) ? Name : $"{Name}\n{Description}"
        : $"{Name} — this effect is not installed any more, so it is skipped on export.";

    /// <summary>Every slider back to the effect's defaults.</summary>
    public void ResetParameters()
    {
        foreach (var parameter in Parameters) parameter.Reset();
    }
}
