using System.Diagnostics.CodeAnalysis;

namespace Pagewing.Core.Forms;

/// <summary>
/// The field flags (<c>/Ff</c>) with the bit values of PDF 32000-1, tables 221, 226, 228 and 230. The PDFium header
/// <c>fpdf_formfill.h</c> names only ReadOnly, Required, NoExport, Multiline, Password, Combo, Edit and MultiSelect; the
/// rest comes from the specification.
/// </summary>
[Flags]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The name FormFieldFlags is fixed by the M11 plan and mirrors the PDF field flags (/Ff).")]
public enum FormFieldFlags : uint
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>The user cannot change the value.</summary>
    ReadOnly = 1,

    /// <summary>The field must have a value for a submit.</summary>
    Required = 2,

    /// <summary>The field is not exported by a submit.</summary>
    NoExport = 4,

    /// <summary>A text field that takes several lines.</summary>
    Multiline = 1u << 12,

    /// <summary>A text field that hides its text.</summary>
    Password = 1u << 13,

    /// <summary>A radio button group that cannot be switched off by clicking the chosen button.</summary>
    NoToggleToOff = 1u << 14,

    /// <summary>A choice field that is a combo box.</summary>
    Combo = 1u << 17,

    /// <summary>A combo box that also takes typed text.</summary>
    Edit = 1u << 18,

    /// <summary>A text field whose value is a file name.</summary>
    FileSelect = 1u << 20,

    /// <summary>A list box that allows several choices.</summary>
    MultiSelect = 1u << 21,

    /// <summary>The text of the field is not spell-checked.</summary>
    DoNotSpellCheck = 1u << 22,

    /// <summary>The text field does not scroll beyond its box.</summary>
    DoNotScroll = 1u << 23,

    /// <summary>A text field divided into equally spaced cells.</summary>
    Comb = 1u << 24,

    /// <summary>A text field with rich text.</summary>
    RichText = 1u << 25,
}
