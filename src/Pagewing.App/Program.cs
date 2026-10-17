using System.Globalization;
using Avalonia;
using Pagewing.App.Resources;

namespace Pagewing.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        if (options.Language is { } language)
        {
            try
            {
                Strings.Culture = new CultureInfo(language);
            }
            catch (CultureNotFoundException)
            {
                // Unknown language code: keep the system language.
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
