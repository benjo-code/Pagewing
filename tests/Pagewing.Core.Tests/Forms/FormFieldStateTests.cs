using Pagewing.Core.Forms;
using Xunit;

namespace Pagewing.Core.Tests.Forms;

public sealed class FormFieldStateTests
{
    [Fact]
    public void StatesWithTheSameSelectionAreEqual()
    {
        var a = FormFieldState.OfSelection([1, 3]);
        var b = FormFieldState.OfSelection(new List<int> { 1, 3 });

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, FormFieldState.OfSelection([3, 1]));
        Assert.NotEqual(a, FormFieldState.OfSelection([1]));
    }

    [Fact]
    public void RadioStateIsTheExportValueOfTheChosenOption()
    {
        var chosen = FormFieldState.OfText("Ja");
        var none = FormFieldState.OfText(null);

        Assert.Equal("Ja", chosen.Text);
        Assert.Null(none.Text);
        Assert.NotEqual(chosen, none);
        Assert.Empty(chosen.SelectedOptions);
        Assert.Null(chosen.Checked);
    }

    [Fact]
    public void CheckedAndTextStatesDiffer()
    {
        Assert.Equal(FormFieldState.OfChecked(true), FormFieldState.OfChecked(true));
        Assert.NotEqual(FormFieldState.OfChecked(true), FormFieldState.OfChecked(false));
        Assert.True(FormFieldState.OfChecked(true).Checked);
        Assert.NotEqual(FormFieldState.OfText(null), FormFieldState.OfChecked(false));
    }
}
