using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pagewing.App.Controls;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class MiddleEllipsisTextBlockTests
{
    private const string LongName = "Sehr langer Dateiname für die Jahresabrechnung 2025 final v3.pdf";

    [AvaloniaFact]
    public void KeepsStartAndExtensionWhenShortened()
    {
        var text = Show(LongName, 200);

        Assert.StartsWith("Sehr", text.DisplayText, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", text.DisplayText, StringComparison.Ordinal);
        Assert.Contains('…', text.DisplayText);
        Assert.True(text.DesiredSize.Width <= 200, $"desired width {text.DesiredSize.Width}");
        Assert.True(text.Bounds.Width <= 200, $"arranged width {text.Bounds.Width}");

        // About 30 characters of 13 px text fit into 200 px; the text is not shortened far more than needed.
        Assert.True(text.DisplayText.Length > 20, $"'{text.DisplayText}' is shorter than necessary");
    }

    [AvaloniaFact]
    public void ShortTextIsUnchanged()
    {
        var text = Show("hello_world.pdf", 200);

        Assert.Equal("hello_world.pdf", text.DisplayText);
        Assert.True(text.DesiredSize.Width is > 0 and <= 200);
    }

    [AvaloniaFact]
    public void NeverExceedsMaxWidthEvenForOneVeryLongWord()
    {
        foreach (var maxWidth in new[] { 120.0, 37.0, 8.0, 1.0 })
        {
            var text = Show(new string('W', 400) + ".pdf", maxWidth);

            Assert.True(text.DesiredSize.Width <= maxWidth, $"max {maxWidth}: desired width {text.DesiredSize.Width} for '{text.DisplayText}'");
            Assert.DoesNotContain("WWWWWWWWWWWWWWWWWWWWWWWWWWWWWW", text.DisplayText, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void ArrangingNarrowerThanMeasuredShortensAgain()
    {
        var text = new MiddleEllipsisTextBlock { Text = LongName };
        text.Measure(new Avalonia.Size(400, 40));
        text.Arrange(new Avalonia.Rect(0, 0, 150, 40));

        Assert.EndsWith(".pdf", text.DisplayText, StringComparison.Ordinal);
        Assert.True(text.MeasureDisplayText() <= 150, $"'{text.DisplayText}' is wider than 150");
    }

    private static MiddleEllipsisTextBlock Show(string value, double maxWidth)
    {
        var text = new MiddleEllipsisTextBlock
        {
            Text = value,
            MaxWidth = maxWidth,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        };
        var window = new Window { Width = 800, Height = 100, Content = text };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Close();
        return text;
    }
}
