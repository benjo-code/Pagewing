using System.Text.RegularExpressions;
using Pagewing.App.Platform.Linux;
using Pagewing.Core.Printing;
using Xunit;

namespace Pagewing.App.Tests;

public sealed partial class CupsOptionsTests
{
    private static string Value(IReadOnlyList<(string Name, string Value)> options, string name) =>
        options.Single(option => option.Name == name).Value;

    private static IReadOnlyList<(string Name, string Value)> Build(PrintSettings settings, int pageCount = 10, int currentPage = 0) =>
        CupsOptions.Build(settings, PrintPlan.Create(pageCount, currentPage, settings));

    [Fact]
    public void AllPagesOmitsPageRanges()
    {
        var options = Build(new PrintSettings());

        Assert.DoesNotContain(options, option => option.Name == "page-ranges");
        Assert.Equal("1", Value(options, "copies"));
    }

    [Fact]
    public void RangeIsNormalized()
    {
        var options = Build(new PrintSettings { Pages = PrintPageChoice.Range, Range = "3,1-2,7" });

        Assert.Equal("1-3,7", Value(options, "page-ranges"));
    }

    [Fact]
    public void TheCurrentPageIsOneBased()
    {
        var options = Build(new PrintSettings { Pages = PrintPageChoice.CurrentPage }, currentPage: 4);

        Assert.Equal("5", Value(options, "page-ranges"));
    }

    [Theory]
    [InlineData(true, "separate-documents-collated-copies")]
    [InlineData(false, "separate-documents-uncollated-copies")]
    public void CollatedAndUncollatedMapToTheCupsValues(bool collate, string expected)
    {
        Assert.Equal(expected, Value(Build(new PrintSettings { Copies = 3, Collate = collate }), "multiple-document-handling"));
    }

    [Theory]
    [InlineData(PrintDuplex.Off, "one-sided")]
    [InlineData(PrintDuplex.LongEdge, "two-sided-long-edge")]
    [InlineData(PrintDuplex.ShortEdge, "two-sided-short-edge")]
    public void DuplexMapsToSides(PrintDuplex duplex, string expected)
    {
        Assert.Equal(expected, Value(Build(new PrintSettings { Duplex = duplex }), "sides"));
    }

    [Fact]
    public void DefaultsLeaveTheChoiceToThePrinter()
    {
        var options = Build(new PrintSettings());

        Assert.DoesNotContain(options, option => option.Name == "sides");
        Assert.DoesNotContain(options, option => option.Name == "orientation-requested");
    }

    [Theory]
    [InlineData(PrintOrientation.Portrait, "3")]
    [InlineData(PrintOrientation.Landscape, "4")]
    public void OrientationMapsToRequested(PrintOrientation orientation, string expected)
    {
        Assert.Equal(expected, Value(Build(new PrintSettings { Orientation = orientation }), "orientation-requested"));
    }

    [Theory]
    [InlineData(PrintScaling.FitToPaper, "fit")]
    [InlineData(PrintScaling.ActualSize, "none")]
    public void ScalingMapsToPrintScaling(PrintScaling scaling, string expected)
    {
        Assert.Equal(expected, Value(Build(new PrintSettings { Scaling = scaling }), "print-scaling"));
    }

    [Fact]
    public void CopiesAreClamped()
    {
        Assert.Equal("999", Value(Build(new PrintSettings { Copies = 5000 }), "copies"));
        Assert.Equal("1", Value(Build(new PrintSettings { Copies = -2 }), "copies"));
    }

    [Fact]
    public void NoValueEverContainsFreeText()
    {
        // The printer name and the typed range never reach an option; only numbers and fixed words do.
        var random = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var settings = new PrintSettings
            {
                Printer = "x; rm -rf / \"quoted\" \n",
                Pages = (PrintPageChoice)random.Next(0, 3),
                Range = random.Next(0, 3) == 0 ? "1,2" : $"{random.Next(1, 5)}-{random.Next(5, 10)}",
                Copies = random.Next(-5, 1200),
                Collate = random.Next(2) == 0,
                Scaling = (PrintScaling)random.Next(0, 2),
                Duplex = (PrintDuplex)random.Next(0, 4),
                Orientation = (PrintOrientation)random.Next(0, 3),
            };
            if (settings.Pages == PrintPageChoice.Range && settings.Range == "1,2")
            {
                settings = settings with { Range = "1-2" };
            }

            foreach (var (name, value) in Build(settings))
            {
                Assert.Matches(OptionName(), name);
                Assert.Matches(OptionValue(), value);
            }
        }
    }

    [GeneratedRegex("^[a-z-]+$")]
    private static partial Regex OptionName();

    [GeneratedRegex("^([0-9,-]+|[a-z]+(-[a-z]+)*)$")]
    private static partial Regex OptionValue();
}
