namespace Pagewing.Core.Forms;

/// <summary>What the document says about its form, read once per document.</summary>
/// <param name="HasFields">Whether the document has any form field.</param>
/// <param name="Hints">What the form contains that Pagewing does not run.</param>
/// <param name="FieldCount">The number of form fields (widgets) counted.</param>
/// <param name="PagesWithFields">The zero-based pages with at least one widget, ascending.</param>
public sealed record FormInfo(bool HasFields, FormHint Hints, int FieldCount, IReadOnlyList<int> PagesWithFields)
{
    /// <summary>
    /// The names of the required fields that have no value: fields with <see cref="FormFieldFlags.Required"/> and no lock,
    /// whose text is empty, which have no chosen option, or whose check box or radio group is not chosen. Widgets of one
    /// field count once, whatever pages they are on; the first widget in the given order decides. Buttons and signature
    /// fields never count.
    /// </summary>
    /// <param name="fieldsOfAllPages">The widgets of all pages.</param>
    /// <returns>The field names in the order of their first widget.</returns>
    public static IReadOnlyList<string> EmptyRequired(IEnumerable<FormField> fieldsOfAllPages)
    {
        ArgumentNullException.ThrowIfNull(fieldsOfAllPages);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var field in fieldsOfAllPages)
        {
            if (!field.Flags.HasFlag(FormFieldFlags.Required) || field.Lock != FormLock.None || !seen.Add(field.Key.FieldName))
            {
                continue;
            }

            if (IsEmpty(field))
            {
                result.Add(field.Key.FieldName);
            }
        }

        return result;
    }

    private static bool IsEmpty(FormField field) => field.Kind switch
    {
        FormFieldKind.Text or FormFieldKind.RadioButton => string.IsNullOrEmpty(field.State.Text),
        FormFieldKind.CheckBox => field.State.Checked != true,
        FormFieldKind.ComboBox or FormFieldKind.ListBox => field.State.SelectedOptions.Count == 0 && string.IsNullOrEmpty(field.State.Text),
        _ => false,
    };
}
