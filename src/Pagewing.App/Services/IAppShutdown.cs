using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Pagewing.App.Services;

/// <summary>Ends the app the regular way, as when its last window is closed: what an installer does once it took over.</summary>
public interface IAppShutdown
{
    /// <summary>Ends the app; may be called on any thread, returns at once (the windows close afterwards on the UI thread).</summary>
    void Shutdown();
}

/// <summary>Ends a desktop app: its windows close and <see cref="IClassicDesktopStyleApplicationLifetime.Exit"/> runs.</summary>
internal sealed class DesktopShutdown(IClassicDesktopStyleApplicationLifetime desktop) : IAppShutdown
{
    public void Shutdown() => Dispatcher.UIThread.Post(() => desktop.Shutdown());
}
