using Avalonia.Input;
using Pagewing.App.Resources;

namespace Pagewing.App.Commands;

/// <summary>
/// The text of a shortcut as tooltips, the help card and the palette show it, in the language of the interface:
/// modifiers first ("Strg+Umschalt+1", "Ctrl+Shift+1"), joined with "+".
/// </summary>
public static class GestureText
{
    /// <summary>Gets the text of <paramref name="gesture"/>, for example "Strg+Plus" or "Bild auf".</summary>
    public static string Of(KeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var parts = new List<string>(4);
        var modifiers = gesture.KeyModifiers;
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add(Strings.KeyCtrl);
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add(Strings.KeyShift);
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Meta");
        }

        parts.Add(KeyName(gesture.Key));
        return string.Join('+', parts);
    }

    /// <summary>The name of a key: digits of both blocks as digits, plus and minus as words, the rest as printed on it.</summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        Key.OemPlus or Key.Add => "Plus",
        Key.OemMinus or Key.Subtract => "Minus",
        Key.OemComma => Strings.KeyComma,
        Key.PageUp => Strings.KeyPageUp,
        Key.PageDown => Strings.KeyPageDown,
        Key.Left => Strings.KeyLeft,
        Key.Right => Strings.KeyRight,
        Key.Escape => "Esc",
        _ => key.ToString(),
    };
}
