using Pagewing.Core.Forms;
using Pagewing.Core.Text;

namespace Pagewing.Core.Tests.Forms;

/// <summary>Builders for form fields in the tests.</summary>
internal static class FormTestFields
{
    public static FormField Make(
        string name = "f",
        int page = 0,
        int widget = 0,
        FormFieldKind kind = FormFieldKind.Text,
        FormFieldTraits traits = FormFieldTraits.None,
        FormFieldState? state = null,
        FormLock fieldLock = FormLock.None,
        int? maxLength = null,
        string? exportValue = null) =>
        new(
            new FormWidgetKey(name, page, widget),
            0,
            kind,
            traits,
            new PageRect(0, 0, 10, 10),
            null,
            exportValue,
            maxLength,
            [],
            state ?? FormFieldState.OfText(null),
            fieldLock,
            null);
}
