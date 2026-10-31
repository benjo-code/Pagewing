using Pagewing.Core.Forms;
using Xunit;
using static Pagewing.Core.Tests.Forms.FormTestFields;

namespace Pagewing.Core.Tests.Forms;

public sealed class FormNavigationTests
{
    [Fact]
    public void NextSkipsReadOnlyAndUnsupportedFields()
    {
        FormField[] fields =
        [
            Make("a"),
            Make("b", fieldLock: FormLock.ReadOnly),
            Make("c", fieldLock: FormLock.Unsupported),
            Make("d"),
        ];

        Assert.Equal(fields[3].Key, FormNavigation.Next(fields, fields[0].Key, forward: true));
        Assert.Equal(fields[0].Key, FormNavigation.Next(fields, fields[3].Key, forward: false));
    }

    [Fact]
    public void NextAtTheEndReturnsNullSoTabCanLeave()
    {
        FormField[] fields = [Make("a"), Make("b"), Make("c", fieldLock: FormLock.ReadOnly)];

        Assert.Equal(fields[1].Key, FormNavigation.Next(fields, fields[0].Key, forward: true));
        Assert.Null(FormNavigation.Next(fields, fields[1].Key, forward: true));
    }

    [Fact]
    public void PreviousAtTheStartReturnsNull()
    {
        FormField[] fields = [Make("a", fieldLock: FormLock.ReadOnly), Make("b"), Make("c")];

        Assert.Null(FormNavigation.Next(fields, fields[1].Key, forward: false));
        Assert.Equal(fields[1].Key, FormNavigation.Next(fields, fields[2].Key, forward: false));
    }

    [Fact]
    public void NextWithoutCurrentStartsAtTheFirstEditableField()
    {
        FormField[] fields = [Make("a", fieldLock: FormLock.ReadOnly), Make("b"), Make("c")];

        Assert.Equal(fields[1].Key, FormNavigation.Next(fields, null, forward: true));
        Assert.Equal(fields[2].Key, FormNavigation.Next(fields, null, forward: false));
    }

    [Fact]
    public void ACurrentWidgetThatIsNotInTheListStartsFromTheOutside()
    {
        FormField[] fields = [Make("a"), Make("b")];

        Assert.Equal(fields[0].Key, FormNavigation.Next(fields, new FormWidgetKey("gone", 0, 0), forward: true));
    }

    [Fact]
    public void AnEmptyListHasNoNext()
    {
        Assert.Null(FormNavigation.Next([], null, forward: true));
    }
}
