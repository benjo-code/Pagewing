using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void MainWindowShowsAndRendersAFrame()
    {
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame.PixelSize.Width > 0);
        window.Close();
    }
}
