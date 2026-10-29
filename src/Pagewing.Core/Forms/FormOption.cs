namespace Pagewing.Core.Forms;

/// <summary>One option of a combo box or list box; the PDFium header gives only the label, no export value.</summary>
/// <param name="Label">The text shown for the option.</param>
public sealed record FormOption(string Label);
