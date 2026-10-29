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
            Make("empty", traits: FormFieldTraits.Required),
            Make("filled", traits: FormFieldTraits.Required, state: FormFieldState.OfText("x")),
            Make("locked", traits: FormFieldTraits.Required, fieldLock: FormLock.ReadOnly),
            Make("optional"),
            Make("combo", kind: FormFieldKind.ComboBox, traits: FormFieldTraits.Required, state: FormFieldState.OfSelection([2])),
            Make("list", kind: FormFieldKind.ListBox, traits: FormFieldTraits.Required, state: FormFieldState.OfSelection([])),
        ];

        Assert.Equal(["empty", "list"], FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void RadioGroupWithoutSelectionCountsAsEmpty()
    {
        FormField[] fields =
        [
            Make("group", widget: 0, kind: FormFieldKind.RadioButton, traits: FormFieldTraits.Required, state: FormFieldState.OfText(null), exportValue: "A"),
            Make("group", widget: 1, kind: FormFieldKind.RadioButton, traits: FormFieldTraits.Required, state: FormFieldState.OfText(null), exportValue: "B"),
            Make("chosen", kind: FormFieldKind.RadioButton, traits: FormFieldTraits.Required, state: FormFieldState.OfText("A"), exportValue: "A"),
        ];

        Assert.Equal(["group"], FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void RadioGroupSplitOverTwoPagesCountsOnce()
    {
        FormField[] fields =
        [
            Make("group", page: 0, kind: FormFieldKind.RadioButton, traits: FormFieldTraits.Required, state: FormFieldState.OfText(null)),
            Make("group", page: 1, kind: FormFieldKind.RadioButton, traits: FormFieldTraits.Required, state: FormFieldState.OfText(null)),
        ];

        Assert.Equal(["group"], FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void UncheckedRequiredCheckBoxCountsAsEmpty()
    {
        FormField[] fields =
        [
            Make("off", kind: FormFieldKind.CheckBox, traits: FormFieldTraits.Required, state: FormFieldState.OfChecked(false)),
            Make("on", kind: FormFieldKind.CheckBox, traits: FormFieldTraits.Required, state: FormFieldState.OfChecked(true)),
        ];

        Assert.Equal(["off"], FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void EmptyRequiredIgnoresButtonsAndSignatures()
    {
        FormField[] fields =
        [
            Make("button", kind: FormFieldKind.PushButton, traits: FormFieldTraits.Required),
            Make("sig", kind: FormFieldKind.Signature, traits: FormFieldTraits.Required),
        ];

        Assert.Empty(FormInfo.EmptyRequired(fields));
    }

    [Fact]
    public void ARequiredTextFieldOfOnlyWhitespaceIsFilled()
    {
        FormField[] fields = [Make("blank", traits: FormFieldTraits.Required, state: FormFieldState.OfText(" "))];

        Assert.Empty(FormInfo.EmptyRequired(fields));
    }
}
