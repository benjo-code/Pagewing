using Pagewing.Core.Annotations;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Forms;

public sealed class FormsFixtureTests
{
    [Theory]
    [InlineData(FormsBasic, 0, 17)]
    [InlineData(FormsBasic, 1, 4)]
    [InlineData(FormsCalc, 0, 5)]
    [InlineData(FormsXfa, 0, 1)]
    [InlineData(FormsSigned, 0, 4)]
    [InlineData(FormsRotated, 0, 3)]
    [InlineData(FormsRotated, 1, 3)]
    [InlineData(FormsHostile, 0, 7)]
    public async Task FormsFixturesOpenAndListTheirWidgets(string name, int page, int widgets)
    {
        await using var document = await OpenFixtureAsync(name);

        var annotations = await document.GetAnnotationsAsync(page, TestContext.Current.CancellationToken);

        Assert.Equal(widgets, annotations.Count);
        Assert.All(annotations, a =>
        {
            Assert.Equal("Widget", a.Subtype);
            Assert.Equal(AnnotationKind.Other, a.Kind);
        });
    }

    [Fact]
    public async Task HostileFixtureListsEveryWidgetButTheOneWithoutARect()
    {
        await using var document = await OpenFixtureAsync(FormsHostile);

        var annotations = await document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);

        // Raw /Annots order: Normal, Loop leaf, NoType, NoRect, NoName, Big, LongName, NulName; the widget without /Rect (3) is not listed.
        Assert.Equal([0, 1, 2, 4, 5, 6, 7], annotations.Select(a => a.Index));
    }
}
