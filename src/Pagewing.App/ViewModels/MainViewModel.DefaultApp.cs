using Avalonia.Threading;
using Pagewing.Core.Distribution;
using Pagewing.Core.Settings;

namespace Pagewing.App.ViewModels;

/// <summary>
/// What the windows of one process share for the card "Set Pagewing as the default for PDFs?": whether the question was
/// started already. The question is asked once per process, by the first window that has a sharp page (or shows the
/// start screen), however many windows follow and whatever files a later launch hands over.
/// </summary>
internal sealed class DefaultAppPromptGate
{
    /// <summary>Gets or sets whether a window has started the question.</summary>
    public bool Started { get; set; }
}

/// <summary>
/// The default app for PDF files in a window: the card at the first start and the command "Set Pagewing as default for
/// PDFs". The service is made when it is first needed, never at startup, and the system is only asked after the first
/// sharp page (or the first frame of the start screen), on the thread pool: the UI thread never waits for it.
/// </summary>
public sealed partial class MainViewModel
{
    private IDefaultAppService? _defaultAppService;
    private bool _defaultAppServiceMade;
    private DefaultAppViewModel? _defaultAppPrompt;
    private CancellationTokenSource? _defaultAppCancellation;

    /// <summary>
    /// Gets what makes the default app service, or <see langword="null"/> for a system or a run that has none (the factory
    /// may return <see langword="null"/> too). The app sets it, tests put a fake in; measurement runs have none.
    /// </summary>
    internal Func<IDefaultAppService?> DefaultAppServiceFactory { get; set; } = static () => null;

    /// <summary>Gets what the windows of the process share, so the question is asked once (<see cref="DefaultAppPromptGate"/>).</summary>
    internal DefaultAppPromptGate DefaultAppPromptGate { get; set; } = new();

    /// <summary>Gets the card "Set as the default for PDFs?" while it is shown, or <see langword="null"/>; it is not an overlay.</summary>
    public DefaultAppViewModel? DefaultAppPrompt
    {
        get => _defaultAppPrompt;
        private set
        {
            if (!ReferenceEquals(_defaultAppPrompt, value))
            {
                var old = _defaultAppPrompt;
                _defaultAppPrompt = value;
                OnPropertyChanged();
                old?.Dispose();
            }
        }
    }

    private IDefaultAppService? DefaultAppService
    {
        get
        {
            if (!_defaultAppServiceMade)
            {
                _defaultAppService = DefaultAppServiceFactory();
                _defaultAppServiceMade = true;
            }

            return _defaultAppService;
        }
    }

    /// <summary>Gets whether the command "Set Pagewing as default" is offered: the system has a service that can work.</summary>
    internal bool CanSetDefaultApp() => DefaultAppService is { IsSupported: true };

    /// <summary>
    /// The first sharp page or the first frame of the start screen is there: asks the system, once per process, in a job
    /// of <see cref="DispatcherPriority.Background"/> (after rendering), unless the user chose "Don't ask again".
    /// </summary>
    private void PostDefaultAppCheck()
    {
        if (_disposed || DefaultAppPromptGate.Started || Settings.Current.DefaultAppPrompt == DefaultAppPromptSetting.Never)
        {
            return;
        }

        DefaultAppPromptGate.Started = true;
        Dispatcher.UIThread.Post(() => _ = CheckDefaultAppAsync(), DispatcherPriority.Background);
    }

    private async Task CheckDefaultAppAsync()
    {
        try
        {
            if (_disposed || DefaultAppService is not { IsSupported: true } service)
            {
                return;
            }

            _defaultAppCancellation = new CancellationTokenSource();
            var token = _defaultAppCancellation.Token;

            // On the pool, whatever the service does before its first await; the result comes back to the UI thread.
            var status = await Task.Run(() => service.GetStatusAsync(token), token).ConfigureAwait(true);
            if (status == DefaultAppStatus.NotDefault && CanShowDefaultAppPrompt())
            {
                DefaultAppPrompt = NewDefaultAppPrompt(service);
            }
        }
        catch (Exception)
        {
            // A check that fails or is cancelled shows nothing.
        }
    }

    private bool CanShowDefaultAppPrompt() =>
        !_disposed
        && DefaultAppPrompt is null
        && Overlay == OverlayKind.None
        && Settings.Current.DefaultAppPrompt == DefaultAppPromptSetting.Ask;

    private DefaultAppViewModel NewDefaultAppPrompt(IDefaultAppService service) =>
        new(service.MakeDefaultAsync, () => DefaultAppPrompt = null, NeverAskForDefaultApp);

    /// <summary>"Don't ask again": the choice goes into the settings; a failing write keeps it in memory and the card still closes.</summary>
    private void NeverAskForDefaultApp()
    {
        try
        {
            Settings.Update(current => current with { DefaultAppPrompt = DefaultAppPromptSetting.Never });
        }
        finally
        {
            DefaultAppPrompt = null;
        }
    }

    /// <summary>
    /// The command "Set Pagewing as default for PDFs": works also after "Don't ask again". It uses the card, so the
    /// answer of the system (the hint for its list, a failure) has a place to show; "Done" closes the card again.
    /// </summary>
    internal void SetDefaultAppFromCommand()
    {
        if (DefaultAppService is not { IsSupported: true } service)
        {
            return;
        }

        var prompt = DefaultAppPrompt ??= NewDefaultAppPrompt(service);
        prompt.SetDefaultCommand.Execute(null);
    }

    private void DisposeDefaultApp()
    {
        _defaultAppCancellation?.Cancel();
        _defaultAppCancellation?.Dispose();
        _defaultAppCancellation = null;
        _defaultAppPrompt?.Dispose();
    }
}
