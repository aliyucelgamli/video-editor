using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VideoEditor.App.Ui;

/// <summary>
/// Turns the native title bar dark (Windows 10 20H1+ and Windows 11) so every
/// window matches the app's dark theme instead of the default white chrome.
/// Native behaviors — snap layouts, shadows, resize borders — are preserved.
/// </summary>
public static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeLegacy = 19; // builds before 20H1

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle, int attribute, ref int value, int size);

    /// <summary>Applies dark chrome to every window of the app as it loads.</summary>
    public static void ApplyToAllWindows() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window) Apply(window);
            }));

    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var enabled = 1;
            if (DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
        }
        catch
        {
            // Purely cosmetic — never let chrome styling break a window.
        }
    }
}
