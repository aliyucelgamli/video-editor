using VideoEditor.App.Mvvm;
using VideoEditor.Application.Actions;

namespace VideoEditor.App.ViewModels;

/// <summary>One "Action → Shortcut" row in the shortcuts editor.</summary>
public class ShortcutActionViewModel : ObservableObject
{
    private string _gestureText = string.Empty;
    private bool _isListening;

    public ShortcutActionViewModel(ActionDescriptor descriptor) => Descriptor = descriptor;

    public ActionDescriptor Descriptor { get; }
    public string Name => Descriptor.Name;

    /// <summary>Current gestures, e.g. "Ctrl+Z / Z", or "—" when unassigned.</summary>
    public string GestureText
    {
        get => _gestureText;
        set => SetProperty(ref _gestureText, value);
    }

    /// <summary>True while this row waits for the next key press.</summary>
    public bool IsListening
    {
        get => _isListening;
        set => SetProperty(ref _isListening, value);
    }
}
