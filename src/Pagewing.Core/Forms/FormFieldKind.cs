namespace Pagewing.Core.Forms;

/// <summary>The kind of a form field as far as Pagewing handles it.</summary>
public enum FormFieldKind
{
    /// <summary>A text field (single line, multi line, comb, password or file selection; see <see cref="FormFieldTraits"/>).</summary>
    Text,

    /// <summary>A check box.</summary>
    CheckBox,

    /// <summary>A radio button; the buttons of one group share the field name.</summary>
    RadioButton,

    /// <summary>A combo box (a drop-down list, editable with <see cref="FormFieldTraits.Edit"/>).</summary>
    ComboBox,

    /// <summary>A list box.</summary>
    ListBox,

    /// <summary>A push button; Pagewing never runs its action.</summary>
    PushButton,

    /// <summary>A signature field; Pagewing does not sign.</summary>
    Signature,

    /// <summary>A field of a kind Pagewing does not know.</summary>
    Unknown,
}
