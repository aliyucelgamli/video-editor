using System.Windows;
using System.Windows.Input;
using VideoEditor.App.Ui;
using VideoEditor.App.ViewModels;
using VideoEditor.Application.Actions;

namespace VideoEditor.App;

/// <summary>
/// Keyboard shortcut editor: Action → Shortcut rows grouped by category.
/// Clicking a shortcut arms it; the next key press is captured as the new
/// gesture (Esc cancels). Conflicts are stolen after a confirmation.
/// Changes apply and persist immediately through the view model's callback.
/// </summary>
public partial class ShortcutsWindow : Window
{
    private readonly ShortcutsViewModel _viewModel;

    public ShortcutsWindow(ShortcutMap map, Action onChanged)
    {
        InitializeComponent();
        _viewModel = new ShortcutsViewModel(map, onChanged);
        DataContext = _viewModel;
    }

    private void Gesture_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutActionViewModel row) return;
        _viewModel.StartListening(row);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.Listening is not { } row) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _viewModel.StopListening();
            return;
        }

        if (KeyGestureText.FromKeyEvent(e) is not { } gesture) return; // lone modifier

        var conflict = _viewModel.FindConflict(gesture, row);
        if (conflict != null)
        {
            var answer = MessageBox.Show(
                $"\"{gesture}\" is already assigned to \"{conflict.Name}\".\n\n" +
                $"Move it to \"{row.Name}\"?",
                "Shortcut In Use", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                _viewModel.StopListening();
                return;
            }
        }

        _viewModel.Assign(row, gesture, conflict);
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Reset every shortcut to its default?", "Keyboard Shortcuts",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _viewModel.ResetAll();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
