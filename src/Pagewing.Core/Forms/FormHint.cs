namespace Pagewing.Core.Forms;

/// <summary>What a form contains that Pagewing does not run; shown once per tab and document as a hint.</summary>
[Flags]
public enum FormHint
{
    /// <summary>Nothing to point out.</summary>
    None = 0,

    /// <summary>Fields with a calculation script; calculated fields do not update (no JavaScript).</summary>
    Calculated = 1,

    /// <summary>Fields with a format or validation script; their values are written unchanged.</summary>
    FormatScripts = 2,

    /// <summary>An XFA form, which Pagewing does not support.</summary>
    Xfa = 4,

    /// <summary>A signature field; Pagewing does not support digital signatures.</summary>
    SignatureField = 8,

    /// <summary>A push button; Pagewing never runs its action (whether it submits is not readable).</summary>
    Buttons = 16,
}
