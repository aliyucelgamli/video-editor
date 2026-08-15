using System.Windows;
using System.Windows.Input;
using VideoEditor.App.ViewModels;

namespace VideoEditor.App;

/// <summary>
/// Clip properties, split in two: what every clip has (layer, fades, speed)
/// and what only this kind of clip has (size/position/opacity for visuals,
/// volume for audio, the text editor for titles). Sliders preview live and
/// commit one undo step per drag; each slider carries its edit key in Tag.
/// </summary>
public partial class EventPropertiesWindow : Window
{
    private readonly EventPropertiesViewModel _viewModel;
    private readonly Action? _editText;

    public EventPropertiesWindow(EventPropertiesViewModel viewModel, Action? editText = null)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _editText = editText;
        DataContext = viewModel;
    }

    private void EditText_Click(object sender, RoutedEventArgs e) => _editText?.Invoke();

    private void Slider_BeginEdit(object sender, MouseButtonEventArgs e) =>
        _viewModel.BeginEdit(TagOf(sender));

    private void Slider_EndEdit(object sender, MouseEventArgs e) =>
        _viewModel.EndEdit(TagOf(sender));

    private static string TagOf(object sender) =>
        (sender as FrameworkElement)?.Tag as string ?? string.Empty;
}
