namespace Pagewing.Core.Forms;

/// <summary>The outcome of checking a text for a field.</summary>
/// <param name="Accepted">Whether the text may be written.</param>
/// <param name="Reason">A short English reason when it is refused; <c>null</c> when accepted.</param>
public readonly record struct FormValueCheck(bool Accepted, string? Reason);

/// <summary>
/// What may be written into a text field. A text that breaks a rule is refused as a whole, never shortened or changed.
/// </summary>
public static class FormValueRules
{
    /// <summary>The most characters (UTF-16 code units, the raw text) of any value, whatever the field says.</summary>
    public const int MaxValueLength = 100_000;

    /// <summary>
    /// Checks <paramref name="text"/> for <paramref name="field"/>. Refused are: a text longer than
    /// <see cref="MaxValueLength"/>; a control character (a tab included) and a line break in a field without
    /// <see cref="FormFieldTraits.Multiline"/>; a character outside <see cref="FormCharacters"/> (the reason names them);
    /// more than <see cref="FormField.MaxLength"/> characters. A line break (CR, LF or CR LF) counts as one character for
    /// <see cref="FormField.MaxLength"/>, because <c>/MaxLen</c> counts characters of the value and the break is one of them
    /// however the engine writes it.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <param name="text">The text to write.</param>
    /// <returns>Whether it is accepted, and why not.</returns>
    public static FormValueCheck CheckText(FormField field, string text)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxValueLength)
        {
            return Refuse("The text is too long.");
        }

        var multiline = field.Traits.HasFlag(FormFieldTraits.Multiline);
        var length = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                if (!multiline)
                {
                    return Refuse("The field takes one line only.");
                }

                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                length++;
            }
            else if (char.IsControl(c))
            {
                return Refuse(FormattableString.Invariant($"The text contains the control character U+{(int)c:X4}."));
            }
            else
            {
                length++;
            }
        }

        var unsupported = FormCharacters.UnsupportedIn(text);
        if (unsupported.Count > 0)
        {
            return Refuse("The field cannot show: " + string.Join(' ', unsupported));
        }

        if (field.MaxLength is { } max && length > max)
        {
            return Refuse(FormattableString.Invariant($"The field takes at most {max} characters."));
        }

        return new FormValueCheck(true, null);
    }

    private static FormValueCheck Refuse(string reason) => new(false, reason);
}
