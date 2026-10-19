using Xunit;

namespace Pagewing.App.Tests;

public sealed class StartupOptionsTests
{
    [Fact]
    public void NoArgumentsGiveEmptyOptions()
    {
        var options = StartupOptions.Parse([]);

        Assert.Empty(options.Paths);
        Assert.Null(options.MeasureStartup);
        Assert.Null(options.MeasureSecond);
        Assert.Null(options.MeasureScroll);
        Assert.Null(options.Language);
    }

    [Fact]
    public void RelativePathsBecomeFullPaths()
    {
        var options = StartupOptions.Parse(["doc.pdf"]);

        Assert.Equal([Path.GetFullPath("doc.pdf")], options.Paths);
    }

    [Fact]
    public void MultiplePathsKeepTheirOrder()
    {
        var options = StartupOptions.Parse(["b.pdf", "a.pdf", "c.pdf"]);

        Assert.Equal(
            [Path.GetFullPath("b.pdf"), Path.GetFullPath("a.pdf"), Path.GetFullPath("c.pdf")],
            options.Paths);
    }

    [Fact]
    public void MorePathsAreAllButTheFirstInOrder()
    {
        Assert.Empty(StartupOptions.Parse([]).MorePaths);
        Assert.Empty(StartupOptions.Parse(["a.pdf"]).MorePaths);
        Assert.Equal(
            [Path.GetFullPath("b.pdf"), Path.GetFullPath("c.pdf")],
            StartupOptions.Parse(["a.pdf", "b.pdf", "c.pdf"]).MorePaths);
    }

    [Fact]
    public void MorePathsKeepTheFirstFileWithinTheLimitOfOneOpen()
    {
        var args = Enumerable.Range(0, 30).Select(i => $"f{i}.pdf").ToArray();

        var more = StartupOptions.Parse(args).MorePaths;

        Assert.Equal(ViewModels.MainViewModel.MaxFilesPerOpen - 1, more.Count);
        Assert.Equal(Path.GetFullPath("f1.pdf"), more[0]);
        Assert.Equal(Path.GetFullPath("f19.pdf"), more[^1]);
    }

    [Fact]
    public void MeasurementsHaveNoMorePaths()
    {
        Assert.Empty(StartupOptions.Parse(["--measure-startup", "a.pdf", "b.pdf", "c.pdf"]).MorePaths);
        Assert.Empty(StartupOptions.Parse(["--measure-second", "a.pdf", "b.pdf", "c.pdf", "d.pdf"]).MorePaths);
        Assert.Empty(StartupOptions.Parse(["--measure-scroll", "a.pdf", "b.pdf", "c.pdf"]).MorePaths);
    }

    [Fact]
    public void PathWithSpacesIsKeptAsOnePath()
    {
        var options = StartupOptions.Parse(["my documents/annual report.pdf"]);

        Assert.Equal([Path.GetFullPath("my documents/annual report.pdf")], options.Paths);
    }

    [Fact]
    public void LanguageOptionTakesTheNextArgument()
    {
        var options = StartupOptions.Parse(["--lang", "de", "doc.pdf"]);

        Assert.Equal("de", options.Language);
        Assert.Equal([Path.GetFullPath("doc.pdf")], options.Paths);
    }

    [Fact]
    public void UnknownOptionIsIgnored()
    {
        var options = StartupOptions.Parse(["--bogus", "doc.pdf"]);

        Assert.Equal([Path.GetFullPath("doc.pdf")], options.Paths);
        Assert.Null(options.Language);
    }

    [Fact]
    public void MeasureOptionsAreParsed()
    {
        var options = StartupOptions.Parse(
            ["--measure-startup", "a.pdf", "--measure-second", "b.pdf", "c.pdf", "--measure-scroll", "d.pdf"]);

        Assert.Equal("a.pdf", options.MeasureStartup);
        Assert.Equal(("b.pdf", "c.pdf"), options.MeasureSecond);
        Assert.Equal("d.pdf", options.MeasureScroll);
        Assert.Empty(options.Paths);
    }

    [Fact]
    public void ZoomOptionTakesTheNextArgumentAsGiven()
    {
        var options = StartupOptions.Parse(["--measure-scroll", "d.pdf", "--zoom", "200"]);

        Assert.Equal("d.pdf", options.MeasureScroll);
        Assert.Equal("200", options.MeasureZoom);
        Assert.Empty(options.Paths);
        Assert.Null(StartupOptions.Parse(["--measure-scroll", "d.pdf"]).MeasureZoom);
    }

    [Fact]
    public void ZoomOptionWithoutValueIsKeptAsEmptySoTheMeasurementFails()
    {
        var options = StartupOptions.Parse(["--measure-scroll", "d.pdf", "--zoom"]);

        Assert.Equal(string.Empty, options.MeasureZoom);
    }

    [Fact]
    public void MeasureSettingsTakesTheNextArgumentAsGiven()
    {
        var options = StartupOptions.Parse(["--measure-startup", "a.pdf", "--measure-settings", "dark.json"]);

        Assert.Equal("dark.json", options.MeasureSettings);
        Assert.Empty(options.Paths);
        Assert.Null(StartupOptions.Parse(["--measure-startup", "a.pdf"]).MeasureSettings);
    }

    [Fact]
    public void MeasureSettingsWithoutValueIsKeptAsEmptySoTheMeasurementFails()
    {
        var options = StartupOptions.Parse(["--measure-startup", "a.pdf", "--measure-settings"]);

        Assert.Equal(string.Empty, options.MeasureSettings);
    }

    [Fact]
    public void MeasureSecondWithoutSecondArgumentIsIgnored()
    {
        var options = StartupOptions.Parse(["--measure-second", "b.pdf"]);

        Assert.Null(options.MeasureSecond);
        Assert.Empty(options.Paths);
    }

    [Fact]
    public void EmptyAndWhitespaceArgumentsAreSkipped()
    {
        var options = StartupOptions.Parse(["", "   ", "doc.pdf", "\t"]);

        Assert.Equal([Path.GetFullPath("doc.pdf")], options.Paths);
    }

    [Fact]
    public void DoubleDashAloneIsIgnored()
    {
        var options = StartupOptions.Parse(["--"]);

        Assert.Empty(options.Paths);
        Assert.Null(options.Language);
    }

    [Fact]
    public void InvalidPathTextIsKeptInsteadOfFailing()
    {
        // GetFullPath rejects a NUL character; the path is kept as given and fails later as a CannotRead error card.
        var options = StartupOptions.Parse(["bad\0name.pdf"]);

        Assert.Equal(["bad\0name.pdf"], options.Paths);
    }

    [Fact]
    public void OptionWithoutValueAtTheEndIsIgnored()
    {
        var options = StartupOptions.Parse(["doc.pdf", "--lang"]);

        Assert.Null(options.Language);
        Assert.Equal([Path.GetFullPath("doc.pdf")], options.Paths);
    }
}
