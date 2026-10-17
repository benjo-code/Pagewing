using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Creating the engine does not start PDFium; that happens with the first document.
            var session = new ViewerSession(new PdfEngine());
            desktop.MainWindow = new MainWindow { DataContext = new MainViewModel(session) };
            desktop.Exit += (_, _) => session.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
