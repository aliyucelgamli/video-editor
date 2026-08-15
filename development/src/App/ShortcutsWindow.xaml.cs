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
    private readonly IDialogService _dialogs = new DialogService();

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
            var move = _dialogs.Confirm(
                "Shortcut In Use",
                $"\"{gesture}\" is already assigned to \"{conflict.Name}\".",
                confirmText: $"Move to {row.Name}",
                cancelText: "Keep As Is",
                details: $"\"{conflict.Name}\" keeps its other shortcuts, if it has any.");
            if (!move)
            {
                _viewModel.StopListening();
                return;
            }
        }

        _viewModel.Assign(row, gesture, conflict);
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogs.Confirm(
                "Keyboard Shortcuts", "Reset every shortcut to its default?",
                confirmText: "Reset All", cancelText: "Cancel", destructive: true))
            _viewModel.ResetAll();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
