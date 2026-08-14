using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using VideoEditor.App.Mvvm;
using VideoEditor.Application.Commands;
using VideoEditor.Application.Effects;
using VideoEditor.Domain;
using VideoEditor.Domain.Effects;

namespace VideoEditor.App.ViewModels;

/// <summary>What the effects panel needs to know about the selected event.</summary>
public record SelectedEventContext(TimelineEvent Event, Track Track, MediaType ContentType);

/// <summary>
/// The Effects tab: the catalog of available effects (built-in + .vefx) and
/// the effect chain of the selected event.
/// </summary>
public class EffectsPanelViewModel : ObservableObject
{
    private readonly EffectCatalog _catalog;
    private readonly UserEffectLibrary _userEffects;
    private readonly Action<IEditorCommand> _execute;
    private readonly Func<SelectedEventContext?> _getSelected;
    private readonly Action<string> _setStatus;
    private readonly Action _previewRefresh;

    public EffectsPanelViewModel(
        EffectCatalog catalog,
        UserEffectLibrary userEffects,
        Action<IEditorCommand> execute,
        Func<SelectedEventContext?> getSelected,
        Action<string> setStatus,
        Action previewRefresh)
    {
        _catalog = catalog;
        _userEffects = userEffects;
        _execute = execute;
        _getSelected = getSelected;
        _setStatus = setStatus;
        _previewRefresh = previewRefresh;

        ImportVefxCommand = new RelayCommand(ImportVefxDialog);
        _catalog.Changed += (_, _) => RefreshCatalog();
        RefreshCatalog();
    }

    public ObservableCollection<EffectDefinitionViewModel> AvailableEffects { get; } = new();
    public ObservableCollection<AppliedEffectViewModel> AppliedEffects { get; } = new();
    public RelayCommand ImportVefxCommand { get; }

    public bool HasSelection => _getSelected() != null;
    public string SelectionName => _getSelected()?.Event.Name ?? string.Empty;
    public bool SelectionHasEffects => AppliedEffects.Count > 0;

    // ---------- Catalog ----------

    private void RefreshCatalog()
    {
        AvailableEffects.Clear();
        foreach (var definition in _catalog.All)
            AvailableEffects.Add(new EffectDefinitionViewModel(definition));
    }

    private void ImportVefxDialog()
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Video Editor Effect (*.vefx)|*.vefx|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        ImportVefxFiles(dialog.FileNames);
    }

    /// <summary>Imports .vefx files (from the dialog or drag &amp; drop).</summary>
    public void ImportVefxFiles(IEnumerable<string> paths)
    {
        int imported = 0, failed = 0;
        string? lastName = null;
        foreach (var path in paths)
        {
            try
            {
                lastName = _userEffects.Import(path).Name;
                imported++;
            }
            catch (Exception ex)
            {
                failed++;
                MessageBox.Show(
                    $"'{System.IO.Path.GetFileName(path)}' could not be imported.\n\n{ex.Message}",
                    "Effect Import Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (imported > 0)
            _setStatus(imported == 1
                ? $"Imported effect '{lastName}'"
                : $"Imported {imported} effect(s)" + (failed > 0 ? $", {failed} failed" : ""));
    }

    // ---------- Applying to events ----------

    /// <summary>Applies an effect (by id) to the given event; validates target compatibility.</summary>
    public bool ApplyEffect(string effectId, TimelineEvent evt, Track track, MediaType contentType)
    {
        if (_catalog.Find(effectId) is not { } definition) return false;

        if (!definition.CanApplyTo(contentType))
        {
            _setStatus($"'{definition.Name}' cannot be applied to {contentType} clips " +
                       $"(targets: {definition.Targets})");
            return false;
        }

        _execute(new AddEffectCommand(evt.Effects, definition.CreateInstance(), definition.Name, evt.Name));
        _setStatus($"Applied '{definition.Name}' to '{evt.Name}'");
        return true;
    }

    /// <summary>Double-click in the effect list: applies to the current selection.</summary>
    public void ApplyEffectToSelection(string effectId)
    {
        if (_getSelected() is not { } selected)
        {
            _setStatus("Select a clip on the timeline first, then double-click an effect");
            return;
        }
        ApplyEffect(effectId, selected.Event, selected.Track, selected.ContentType);
    }

    // ---------- Selected event chain ----------

    /// <summary>Rebuilds the applied-effects list for the current selection.</summary>
    public void RefreshSelection()
    {
        AppliedEffects.Clear();
        if (_getSelected() is { } selected)
        {
            foreach (var instance in selected.Event.Effects)
                AppliedEffects.Add(new AppliedEffectViewModel(
                    instance,
                    _catalog.Find(instance.Type),
                    onRemove: RemoveEffect,
                    onSetEnabled: SetEnabled,
                    onParameterCommitted: CommitParameter,
                    onParameterPreview: _previewRefresh));
        }

        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionName));
        OnPropertyChanged(nameof(SelectionHasEffects));
    }

    private void RemoveEffect(AppliedEffectViewModel effect)
    {
        if (_getSelected() is not { } selected) return;
        _execute(new RemoveEffectCommand(selected.Event.Effects, effect.Instance, effect.Name));
    }

    private void SetEnabled(EffectInstance instance, bool enabled) =>
        _execute(new SetEffectEnabledCommand(instance, enabled));

    private void CommitParameter(
        EffectInstance instance, EffectParameterDefinition parameter, double oldValue, double newValue)
    {
        // The slider already set the live value; the command records it for undo.
        instance.Parameters[parameter.Key] = oldValue;
        _execute(new SetEffectParameterCommand(instance, parameter.Key, newValue));
    }
}
