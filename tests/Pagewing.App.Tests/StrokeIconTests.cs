using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Pagewing.App.Controls;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class StrokeIconTests
{
    private static readonly string[] IconNames =
        ["FileText", "FolderOpen", "X", "ChevronLeft", "ChevronRight", "Minus", "Plus", "Lock", "AlertTriangle"];

    [AvaloniaFact]
    public void AllIconsOfMilestone3AreStreamGeometries()
    {
        foreach (var name in IconNames)
        {
            Assert.IsType<StreamGeometry>(TokenTests.Resource($"Pw.Icon.{name}", ThemeVariant.Light));
        }
    }

    [AvaloniaFact]
    public void PlusIconDrawsAStrokeNotAFill()
    {
        using var probe = Render("Plus", 24);

        // The crossing of the two bars is inked, the corner is empty.
        Assert.True(probe.IsInked(12, 12), "cross centre");
        Assert.True(probe.IsInked(11, 6), "vertical bar");
        Assert.True(probe.IsInked(6, 11), "horizontal bar");
        Assert.True(probe.IsBlank(1, 1), "top left corner");
        Assert.True(probe.IsBlank(22, 22), "bottom right corner");
        Assert.True(probe.IsBlank(7, 7), "between the bars");
    }

    [AvaloniaFact]
    public void CrossIconLeavesTheTriangleRegionsBlank()
    {
        using var probe = Render("X", 24);

        Assert.True(probe.IsInked(12, 12), "crossing of the diagonals");
        Assert.True(probe.IsBlank(12, 8), "top triangle");
        Assert.True(probe.IsBlank(8, 12), "left triangle");
        Assert.True(probe.IsBlank(16, 12), "right triangle");
        Assert.True(probe.IsBlank(12, 16), "bottom triangle");
    }

    [AvaloniaFact]
    public void ClosedOutlineIconsAreNotFilled()
    {
        using var lockProbe = Render("Lock", 24);
        Assert.True(lockProbe.IsInked(5, 17), "left edge of the lock body");
        Assert.True(lockProbe.IsBlank(8, 18), "inside of the lock body");

        using var alertProbe = Render("AlertTriangle", 24);
        Assert.True(alertProbe.IsInked(12, 11), "exclamation mark");
        Assert.True(alertProbe.IsBlank(8, 17), "inside of the warning triangle");

        using var documentProbe = Render("FileText", 24);
        Assert.True(documentProbe.IsInked(12, 13), "text line");
        Assert.True(documentProbe.IsBlank(7, 6), "inside of the document above the text lines");
    }

    [AvaloniaFact]
    public void StrokeThicknessScalesWithTheIconSize()
    {
        using var probe = Render("Plus", 48);

        // 2 units of the 24 grid become 4 pixels: the vertical bar covers x 22 to 26.
        Assert.True(probe.IsInked(22, 10));
        Assert.True(probe.IsInked(25, 10));
        Assert.True(probe.IsBlank(20, 10));
        Assert.True(probe.IsBlank(28, 10));
        Assert.True(probe.IsInked(24, 24));
    }

    [AvaloniaFact]
    public void WithoutABrushTheIconFollowsTheInheritedForeground()
    {
        using var probe = Render("Plus", 24, brush: null, inheritedForeground: Brushes.Black);

        Assert.True(probe.IsInked(12, 12));
    }

    [AvaloniaFact]
    public void DefaultSizeIs16AndTheIconIsDecorative()
    {
        var icon = new StrokeIcon();
        icon.Measure(new Size(100, 100));

        Assert.Equal(new Size(16, 16), icon.DesiredSize);
        Assert.False(icon.IsHitTestVisible);
        Assert.False(icon.Focusable);
    }

    private static FramePixels Render(string iconName, double size, IBrush? brush = null, IBrush? inheritedForeground = null)
    {
        var icon = new StrokeIcon
        {
            Data = (Geometry)TokenTests.Resource($"Pw.Icon.{iconName}", ThemeVariant.Light),
            Brush = brush ?? (inheritedForeground is null ? Brushes.Black : null),
            Width = size,
            Height = size,
        };
        var host = new Panel { Background = Brushes.White, Children = { icon } };
        if (inheritedForeground is not null)
        {
            TextElement.SetForeground(host, inheritedForeground);
        }

        var window = new Window
        {
            Width = size,
            Height = size,
            Content = host,
            RequestedThemeVariant = ThemeVariant.Light,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        window.Close();
        return new FramePixels(frame!);
    }
}
