using System.Windows.Input;

namespace VideoEditor.App.Ui;

/// <summary>
/// Converts between gesture strings ("Ctrl+Shift+Z", "S", "+") and WPF keys.
/// Unlike KeyGesture, plain letters without modifiers are allowed — the
/// timeline uses single-key shortcuts (S, T, I, O…). Format and TryParse
/// round-trip, so formatted strings are safe to persist.
/// </summary>
public static class KeyGestureText
{
    private static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["+"] = Key.OemPlus,
        ["-"] = Key.OemMinus,
        ["Num+"] = Key.Add,
        ["Num-"] = Key.Subtract,
        ["Del"] = Key.Delete,
        ["Esc"] = Key.Escape
    };

    /// <summary>The gesture the user just pressed, or null for a lone modifier.</summary>
    public static string? FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.None
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin) return null;
        return Format(Keyboard.Modifiers, key);
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static bool TryParse(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // "Ctrl++" means Ctrl + the "+" key: only split on separators that
        // have something after them.
        var tokens = new List<string>();
        var current = string.Empty;
        foreach (var character in text)
        {
            if (character == '+' && current.Length > 0 && tokens.Count + 1 < 5 &&
                IsModifierName(current))
            {
                tokens.Add(current);
                current = string.Empty;
            }
            else
            {
                current += character;
            }
        }
        if (current.Length == 0) return false;
        tokens.Add(current);

        for (var i = 0; i < tokens.Count - 1; i++)
        {
            modifiers |= tokens[i].Trim().ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModifierKeys.Control,
                "SHIFT" => ModifierKeys.Shift,
                "ALT" => ModifierKeys.Alt,
                "WIN" => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
        }

        var keyToken = tokens[^1].Trim();
        if (Aliases.TryGetValue(keyToken, out key)) return true;
        return Enum.TryParse(keyToken, ignoreCase: true, out key) && key != Key.None;
    }

    private static bool IsModifierName(string token) =>
        token.Trim().ToUpperInvariant() is "CTRL" or "CONTROL" or "SHIFT" or "ALT" or "WIN";

    private static string KeyName(Key key) => key switch
    {
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.Add => "Num+",
        Key.Subtract => "Num-",
        _ => key.ToString()
    };
}
