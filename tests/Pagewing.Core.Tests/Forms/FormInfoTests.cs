using Pagewing.Core.Forms;
using Xunit;
using static Pagewing.Core.Tests.Forms.FormTestFields;

namespace Pagewing.Core.Tests.Forms;

public sealed class FormInfoTests
{
    [Fact]
    public void EmptyRequiredIgnoresFilledReadOnlyAndOptionalFields()
    {
        FormField[] fields =
        [
            Make("empty", flags: FormFieldFlags.Required),
            Make("filled", flags: FormFieldFlags.Required, state: FormFieldState.OfText("x")),
            Make("locked", flags: FormFieldFlags.Required, fieldLock: FormLock.ReadOnly),
            Make("optional"),
            Make("combo", kind: FormFieldKind.ComboBox, flags: FormFieldFlags.Required, state: FormFieldState.OfSelection([2])),
            Make("list", kind: FormFieldKind.ListBox, flags: FormFieldFlags.Required, state: FormFieldState.OfSelection([])),
        ];

        Assert.Equal(["empty", "list"], FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void RadioGroupWithoutSelectionCountsAsEmpty()
    {
        FormField[] fields =
        [
            Make("group", widget: 0, kind: FormFieldKind.RadioButton, flags: FormFieldFlags.Required, state: FormFieldState.OfText(null), exportValue: "A"),
            Make("group", widget: 1, kind: FormFieldKind.RadioButton, flags: FormFieldFlags.Required, state: FormFieldState.OfText(null), exportValue: "B"),
            Make("chosen", kind: FormFieldKind.RadioButton, flags: FormFieldFlags.Required, state: FormFieldState.OfText("A"), exportValue: "A"),
        ];

        Assert.Equal(["group"], FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void RadioGroupSplitOverTwoPagesCountsOnce()
    {
        FormField[] fields =
        [
            Make("group", page: 0, kind: FormFieldKind.RadioButton, flags: FormFieldFlags.Required, state: FormFieldState.OfText(null)),
            Make("group", page: 1, kind: FormFieldKind.RadioButton, flags: FormFieldFlags.Required, state: FormFieldState.OfText(null)),
        ];

        Assert.Equal(["group"], FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void UncheckedRequiredCheckBoxCountsAsEmpty()
    {
        FormField[] fields =
        [
            Make("off", kind: FormFieldKind.CheckBox, flags: FormFieldFlags.Required, state: FormFieldState.OfChecked(false)),
            Make("on", kind: FormFieldKind.CheckBox, flags: FormFieldFlags.Required, state: FormFieldState.OfChecked(true)),
        ];

        Assert.Equal(["off"], FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void EmptyRequiredIgnoresButtonsAndSignatures()
    {
        FormField[] fields =
        [
            Make("button", kind: FormFieldKind.PushButton, flags: FormFieldFlags.Required),
            Make("sig", kind: FormFieldKind.Signature, flags: FormFieldFlags.Required),
        ];

        Assert.Empty(FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void ARequiredTextFieldOfOnlyWhitespaceIsFilled()
    {
        FormField[] fields = [Make("blank", flags: FormFieldFlags.Required, state: FormFieldState.OfText(" "))];

        Assert.Empty(FormInfo.EmptyRequired(fields));
    }
}
