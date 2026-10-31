using Pagewing.Core.Text;

namespace Pagewing.Core.Forms;

/// <summary>
/// One widget of a form field as read from the document. The field's value (<see cref="State"/>) is the same for all
/// widgets of the field; <see cref="ExportValue"/> belongs to this widget.
/// </summary>
/// <param name="Key">The identity of the widget.</param>
/// <param name="Index">The index of the widget's annotation on its page (moves when annotations change; never an identity).</param>
/// <param name="Kind">The kind of the field.</param>
/// <param name="Traits">The field flags (<c>/Ff</c>).</param>
/// <param name="Bounds">The widget rectangle in page points, origin top left.</param>
/// <param name="AlternateName">The tooltip name (<c>/TU</c>), or <c>null</c>.</param>
/// <param name="ExportValue">The export value of this widget (check box and radio button), or <c>null</c>.</param>
/// <param name="MaxLength">The maximum length of a text value, or <c>null</c> when the field has none.</param>
/// <param name="Options">The options of a combo box or list box.</param>
/// <param name="State">The value of the field.</param>
/// <param name="Lock">Whether Pagewing lets the user change the field.</param>
/// <param name="LockReason">A short reason for a lock, or <c>null</c>.</param>
public sealed record FormField(
    FormWidgetKey Key,
    int Index,
    FormFieldKind Kind,
    FormFieldTraits Traits,
    PageRect Bounds,
    string? AlternateName,
    string? ExportValue,
    int? MaxLength,
    IReadOnlyList<FormOption> Options,
    FormFieldState State,
    FormLock Lock,
    string? LockReason)
{
    /// <summary>Gets the name to show and to announce: the tooltip name, else the field name.</summary>
    public string DisplayName => AlternateName ?? Key.FieldName;
}
