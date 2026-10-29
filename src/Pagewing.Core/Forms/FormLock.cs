namespace Pagewing.Core.Forms;

/// <summary>Whether Pagewing lets the user change a field.</summary>
public enum FormLock
{
    /// <summary>Pagewing can change the field.</summary>
    None,

    /// <summary>The field is read-only (flag or document rights).</summary>
    ReadOnly,

    /// <summary>Pagewing cannot write the field: a signature, a file selection, a button or a field without a name.</summary>
    Unsupported,
}
