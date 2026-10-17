using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Hosting;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App;

/// <summary>What <see cref="Program.Main"/> prepared before Avalonia started.</summary>
/// <param name="Session">The session, possibly already opening the file of the command line.</param>
/// <param name="Instance">The primary single instance, or <see langword="null"/> for an extra instance.</param>
internal sealed record AppStartup(ViewerSession Session, SingleInstance? Instance);

public sealed class App : Application
{
    private readonly AppStartup? _startup;

    /// <summary>Creates the app without startup state (tests, designer): it creates its own session.</summary>
    public App()
    {
    }

    internal App(AppStartup startup) => _startup = startup;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Creating the engine does not start PDFium; that happens with the first document.
            var session = _startup?.Session ?? new ViewerSession(new PdfEngine());
            var instance = _startup?.Instance;
            var window = new MainWindow();
            var viewModel = new MainViewModel(session, new AvaloniaFilePicker(window));
            window.DataContext = viewModel;
            desktop.MainWindow = window;

            if (instance is not null)
            {
                // Later launches are accepted only after the first frame (startup path); Background runs after rendering.
                window.Opened += (_, _) => Dispatcher.UIThread.Post(
                    () => StartListening(instance, viewModel),
                    DispatcherPriority.Background);
            }

            desktop.Exit += (_, _) =>
            {
                instance?.Dispose();
                session.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void StartListening(SingleInstance instance, MainViewModel viewModel)
    {
        try
        {
            instance.StartListening(viewModel.OnExternalOpen);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Closed before the first frame, or the window opened again: nothing (more) to listen for.
        }
    }
}
