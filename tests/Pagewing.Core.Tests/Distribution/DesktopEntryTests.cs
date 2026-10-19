using Pagewing.Core.Distribution;
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
}
