using System.Windows;
using System.Windows.Input;
using VideoEditor.App.ViewModels;

namespace VideoEditor.App;

/// <summary>
/// Per-clip effect window (VEGAS "Event FX" style), opened from the fx button
/// on a clip: add compatible effects with adjustable settings, tweak or toggle
/// the applied chain, remove effects. All changes are undoable and preview
/// live through the shared effects panel view model.
/// </summary>
public partial class EventFxWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Guid _eventId;

    public EventFxWindow(MainViewModel viewModel, Guid eventId, string clipName)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _eventId = eventId;

        DataContext = viewModel;              // Effects.AppliedEffects follows the selection
        _viewModel.SelectEvent(eventId);      // make sure this clip is the selection
        ClipNameText.Text = clipName;
        EffectPicker.ItemsSource = _viewModel.GetCompatibleEffects(eventId);
        if (EffectPicker.Items.Count > 0) EffectPicker.SelectedIndex = 0;
    }

    private void Add_Click(object sender, RoutedEventArgs e) => AddSelectedEffect();

    private void EffectPicker_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is EffectDefinitionViewModel)
            AddSelectedEffect();
    }

    private void AddSelectedEffect()
    {
        if (EffectPicker.SelectedItem is not EffectDefinitionViewModel effect) return;
        _viewModel.ApplyEffectToEvent(effect.Id, _eventId);
    }

    // Slider commit pattern: live value while dragging, one undo step on release.

    private void Param_PreviewMouseDown(object sender, MouseButtonEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as EffectParameterViewModel)?.BeginEdit();

    private void Param_PreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as EffectParameterViewModel)?.EndEdit();

    private void Param_LostMouseCapture(object sender, MouseEventArgs e) =>
        ((sender as FrameworkElement)?.DataContext as EffectParameterViewModel)?.EndEdit();
}
