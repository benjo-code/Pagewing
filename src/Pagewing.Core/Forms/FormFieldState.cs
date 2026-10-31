namespace Pagewing.Core.Forms;

/// <summary>
/// The value of a form field, per field (not per widget). A text field or the text of an editable combo box is
/// <see cref="Text"/>; a check box is <see cref="Checked"/>; a radio group is <see cref="Text"/>, the export value of the
/// chosen option, or <c>null</c> for no choice; a combo box or list box is <see cref="SelectedOptions"/>. Two states are
/// equal when their values are equal, the list compared by content.
/// </summary>
public sealed record FormFieldState
{
    /// <summary>Gets the text, or for a radio group the export value of the chosen option; <c>null</c> for none.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the zero-based indexes of the chosen options of a combo box or list box.</summary>
    public IReadOnlyList<int> SelectedOptions { get; init; } = [];

    /// <summary>Gets whether a check box is checked; <c>null</c> for any other kind of field.</summary>
    public bool? Checked { get; init; }

    /// <summary>Gets the state of a text-like field or a radio group.</summary>
    /// <param name="text">The text, or the export value of the chosen radio option; <c>null</c> for none.</param>
    /// <returns>The state.</returns>
    public static FormFieldState OfText(string? text) => new() { Text = text };

    /// <summary>Gets the state of a check box.</summary>
    /// <param name="isChecked">Whether it is checked.</param>
    /// <returns>The state.</returns>
    public static FormFieldState OfChecked(bool isChecked) => new() { Checked = isChecked };

    /// <summary>Gets the state of a combo box or list box.</summary>
    /// <param name="selected">The indexes of the chosen options (copied).</param>
    /// <returns>The state.</returns>
    public static FormFieldState OfSelection(IReadOnlyList<int> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return new() { SelectedOptions = [.. selected] };
    }

    /// <inheritdoc />
    public bool Equals(FormFieldState? other) =>
        other is not null
        && string.Equals(Text, other.Text, StringComparison.Ordinal)
        && Checked == other.Checked
        && SelectedOptions.SequenceEqual(other.SelectedOptions);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Text, StringComparer.Ordinal);
        hash.Add(Checked);
        foreach (var index in SelectedOptions)
        {
            hash.Add(index);
        }

        return hash.ToHashCode();
    }
}
