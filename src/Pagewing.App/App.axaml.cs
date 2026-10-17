using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Pagewing.App.Measurement;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Hosting;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App;

/// <summary>What <see cref="Program.Main"/> prepared before Avalonia started.</summary>
/// <param name="Session">
/// The session, possibly already opening the file of the command line; the first tab of the window when it does.
/// </param>
/// <param name="Instance">The primary single instance, or <see langword="null"/> for an extra instance or a measurement.</param>
/// <param name="Measurement">The options of a measurement switch, or <see langword="null"/> for a normal launch.</param>
internal sealed record AppStartup(ViewerSession Session, SingleInstance? Instance, StartupOptions? Measurement = null);

public sealed class App : Application
{
    private readonly AppStartup? _startup;

    /// <summary>Creates the app without startup state (tests, designer): its window starts without a tab.</summary>
    public App()
    {
    }

    internal App(AppStartup startup)
    {
        // Avalonia creates the app after the platform (windowing, graphics) is set up.
        StartupPhases.Mark("app-created");
        _startup = startup;
    }

    public override void RegisterServices()
    {
        StartupPhases.Mark("app-register-services");
        base.RegisterServices();
    }

    public override void Initialize()
    {
        // The platform (windowing, graphics) is initialized before this.
        StartupPhases.Mark("app-initialize");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupPhases.Mark("app-initialized");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Creating an engine does not start PDFium; that happens with the first document.
            var session = _startup?.Session;
            var instance = _startup?.Instance;
            var window = new MainWindow();
            StartupPhases.Mark("window-created");
            var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new AvaloniaFilePicker(window));
            var adopted = false;
            if (session is not null && session.State != ViewerState.Empty)
            {
                // The file of the command line, already opening since Program.Main: the first tab, in the first frame.
                viewModel.AdoptSession(session);
                adopted = true;
            }

            window.DataContext = viewModel;
            desktop.MainWindow = window;

            if (instance is not null)
            {
                // Later launches are accepted only after the first frame (startup path); Background runs after rendering.
                window.Opened += (_, _) => Dispatcher.UIThread.Post(
                    () => StartListening(instance, viewModel),
                    DispatcherPriority.Background);
            }

            if (!OperatingSystem.IsWindows())
            {
                // Windows takes the icon of the executable (ApplicationIcon); elsewhere the window carries it. Decoding
                // it is not part of the startup path.
                window.Opened += (_, _) => Dispatcher.UIThread.Post(() => SetWindowIcon(window), DispatcherPriority.Background);
            }

            if (_startup?.Measurement is { } measure)
            {
                // The first frame of the window: the first tick of the render loop after it opened.
                window.Opened += (_, _) =>
                {
                    StartupPhases.Mark("window-opened");
                    window.RequestAnimationFrame(_ => StartupPhases.Mark("first-frame"));
                };

                // Started before the window opens, so no sharp image can come before the measurement listens.
                var output = new MeasurementOutput(
                    Console.Out, Console.Error, code => Dispatcher.UIThread.Post(() => desktop.Shutdown(code)), StartupPhases.Current);
                MeasurementRun.Create(measure, viewModel, window.DocumentView, output)?.Start();
            }

            desktop.Exit += (_, _) =>
            {
                instance?.Dispose();
                viewModel.Dispose();
                if (!adopted)
                {
                    // Unused (no file on the command line); a tab owns and releases an adopted one.
                    session?.Dispose();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void SetWindowIcon(Window window) => window.Icon = LoadWindowIcon();

    internal static WindowIcon LoadWindowIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Pagewing/Assets/pagewing-128.png"));
        return new WindowIcon(stream);
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
