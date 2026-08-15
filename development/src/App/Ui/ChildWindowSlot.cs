using System.Windows;

namespace VideoEditor.App.Ui;

/// <summary>
/// One-instance-at-a-time child window management: opening a new instance
/// closes the previous one, and the slot clears itself when the window
/// closes. Replaces the repeated field + Close + Closed-handler pattern.
/// </summary>
public sealed class ChildWindowSlot<TWindow> where TWindow : Window
{
    private TWindow? _current;

    public void Show(Window owner, Func<TWindow> create)
    {
        _current?.Close();
        var window = create();
        window.Owner = owner;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window)) _current = null;
        };
        _current = window;
        window.Show();
    }

    public void Close() => _current?.Close();
}
