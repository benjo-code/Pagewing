namespace Pagewing.Core.Forms;

/// <summary>The result of writing a field value.</summary>
/// <param name="Before">The state before the write.</param>
/// <param name="After">The state after the write, as read back from the document.</param>
/// <param name="Pages">Every page on which the field has widgets (all of them need a new picture).</param>
public sealed record FormChange(FormFieldState Before, FormFieldState After, IReadOnlySet<int> Pages);
