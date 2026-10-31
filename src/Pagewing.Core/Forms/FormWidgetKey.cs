namespace Pagewing.Core.Forms;

/// <summary>
/// The identity of one widget of a form field. The field name is the identity of the field in the whole document: a field can
/// have widgets on several pages (a radio group, a field placed several times). Never find a widget by the index of its
/// annotation, which moves when annotations are created or deleted.
/// </summary>
/// <param name="FieldName">The fully qualified field name.</param>
/// <param name="Page">The zero-based page of the widget.</param>
/// <param name="WidgetNumber">The running number of this field's widgets on that page, starting at 0.</param>
public readonly record struct FormWidgetKey(string FieldName, int Page, int WidgetNumber);
