using Avalonia;
using Avalonia.Headless;
using Pagewing.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Pagewing.App.Tests;

/// <summary>Configures the headless Avalonia platform used by all UI tests (with Skia so frames can be captured).</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
