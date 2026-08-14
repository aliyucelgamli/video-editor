using System.Windows;
using System.Windows.Input;
using VideoEditor.App.ViewModels;

namespace VideoEditor.App;

/// <summary>
/// Clip properties window (size/position, speed, levels, fades, info) opened
/// from the size or "…" button on a clip. Sliders preview live and commit one
/// undo step per drag; each slider carries its edit key in Tag.
/// </summary>
public partial class EventPropertiesWindow : Window
{
    private readonly EventPropertiesViewModel _viewModel;

    public EventPropertiesWindow(EventPropertiesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Slider_BeginEdit(object sender, MouseButtonEventArgs e) =>
        _viewModel.BeginEdit(TagOf(sender));

    private void Slider_EndEdit(object sender, MouseEventArgs e) =>
        _viewModel.EndEdit(TagOf(sender));

    private static string TagOf(object sender) =>
        (sender as FrameworkElement)?.Tag as string ?? string.Empty;
}
