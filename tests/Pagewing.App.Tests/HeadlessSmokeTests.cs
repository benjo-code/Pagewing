using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void MainWindowShowsAndRendersAFrame()
    {
        using var session = new ViewerSession(new PdfEngine());
        var window = new MainWindow { DataContext = new MainViewModel(session) };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.True(frame.PixelSize.Width > 0);
        window.Close();
    }
}
