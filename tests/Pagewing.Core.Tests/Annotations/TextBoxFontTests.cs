using Pagewing.Core.Annotations;
using Pagewing.Core.Redaction;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>Where the font of the text boxes and the overlay text comes from: one copy, in Core, read without any help of the app (milestone 14.1, task 14).</summary>
public sealed class TextBoxFontTests
{
    [Fact]
    public async Task CliCanLayOutWithoutTheApp()
    {
        // Nothing hands the font over in this process (the test helper that did is gone): the command line is in the same position.
        Assert.True(TextBoxFont.IsAvailable);

        var metrics = await PdfiumThread.Shared.InvokeAsync(
            () => TextBoxFont.MetricsOf(TextBoxFont.Table(PdfiumThread.Shared)),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1.005, metrics.Ascent, 3);
        Assert.InRange(metrics.AdvanceOf('H'), 0.6, 0.8);

        // The overlay layout runs on these metrics and keeps inside the area.
        var area = new PageRect(0, 0, 120, 24);
        var placement = RedactionOverlayLayout.Fit("geschwärzt", area, new RedactionOverlayStyle(), 12, metrics);
        Assert.True(placement.Fits);
        var line = Assert.Single(placement.Lines);
        Assert.True(line.X >= 0 && line.X + line.Width <= area.Right);
        Assert.True(line.Baseline - (metrics.Ascent * placement.FontSize) >= 0);
        Assert.True(line.Baseline + (metrics.Descent * placement.FontSize) <= area.Bottom);
    }

    [Fact]
    public void TheEmbeddedCopyIsTheShippedGeistRegular()
    {
        var embedded = TextBoxFont.ReadData();
        var file = File.ReadAllBytes(TestData.GeistRegular);

        Assert.Equal(file, embedded);
        Assert.StartsWith("src/Pagewing.Core/", Path.GetRelativePath(TestData.Root, TestData.GeistRegular).Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void NoSecondCopyOfGeistInTheApp()
    {
        // The app uses the very file of Core for its own text (an Avalonia resource that links to it); no copy of Geist Regular lies in
        // the project of the app, and the font is embedded in Core once.
        var app = Path.Combine(TestData.Root, "src", "Pagewing.App");
        var copies = Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => Path.GetFileName(path).StartsWith("Geist-Regular", StringComparison.OrdinalIgnoreCase)
                && Path.GetExtension(path) is ".ttf" or ".otf" or ".woff" or ".woff2")
            .ToList();
        Assert.Empty(copies);

        var coreFonts = Directory.EnumerateFiles(Path.Combine(TestData.Root, "src", "Pagewing.Core"), "Geist-Regular.*", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.Single(coreFonts);

        var project = File.ReadAllText(Path.Combine(app, "Pagewing.App.csproj"));
        Assert.Contains(@"..\Pagewing.Core\Assets\Fonts\Geist-Regular.ttf", project, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(app, "Services", "TextBoxFontSource.cs")), "the app has no source of the font of its own any more");
    }
}
