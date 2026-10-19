using Pagewing.Core.Distribution;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Distribution;

public sealed class DesktopEntryTests
{
    [Fact]
    public void ExecEscapesSpacesAndPercent()
    {
        var text = DesktopEntry.Render("/home/a b/Pagewing%1.AppImage", "pagewing");

        Assert.Contains("Exec=\"/home/a b/Pagewing%%1.AppImage\" %F", text.Split('\n'));
    }

    [Fact]
    public void ExecLeavesAPlainPathAlone()
    {
        Assert.Equal("/opt/pagewing/Pagewing", DesktopEntry.EscapeExec("/opt/pagewing/Pagewing"));
    }

    [Fact]
    public void ExecEscapesQuotesDollarAndBackticksInsideQuotes()
    {
        Assert.Equal("\"/a b/\\\"x\\\"\\$y\\`z\"", DesktopEntry.EscapeExec("/a b/\"x\"$y`z"));
    }

    [Fact]
    public void RenderHasMimeTypeIconAndWmClass()
    {
        var lines = DesktopEntry.Render("/opt/Pagewing", "pagewing").Split('\n');

        Assert.Contains("[Desktop Entry]", lines);
        Assert.Contains("MimeType=application/pdf;", lines);
        Assert.Contains("StartupWMClass=Pagewing", lines);
        Assert.Contains("Icon=pagewing", lines);
        Assert.Contains("Exec=/opt/Pagewing %F", lines);
        Assert.EndsWith("\n", DesktopEntry.Render("/opt/Pagewing", "pagewing"), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderIsTheSameForTheTemplateInPackaging()
    {
        var template = File.ReadAllText(Path.Combine(TestData.Root, "packaging", "linux", "pagewing.desktop")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var rendered = DesktopEntry.Render("/usr/lib/pagewing/Pagewing", "pagewing");

        Assert.Equal("Exec=/usr/lib/pagewing/Pagewing %F", template.Split('\n').Single(line => line.StartsWith("Exec=", StringComparison.Ordinal)));
        Assert.Equal(WithoutExec(rendered), WithoutExec(template));
        Assert.Equal(template, rendered);
    }

    [Fact]
    public void WindowClassIsTheOneAvaloniaSetsOnX11()
    {
        // Verified with xprop on the Native AOT build under Xvfb: WM_CLASS(STRING) = "Pagewing", "Pagewing".
        Assert.Contains("StartupWMClass=Pagewing", DesktopEntry.Render("/opt/Pagewing", "pagewing").Split('\n'));
    }

    private static string WithoutExec(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.StartsWith("Exec=", StringComparison.Ordinal)));
}
