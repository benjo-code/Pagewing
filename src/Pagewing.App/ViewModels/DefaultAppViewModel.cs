using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Distribution;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The card "Set Pagewing as the default for PDFs?" (<see cref="MainViewModel.DefaultAppPrompt"/>): "Set as default",
/// "Later" (this session only) and "Don't ask again". After the system asked the user to choose
/// (<see cref="DefaultAppResult.OpenedSystemSettings"/>) or the attempt failed, the card stays with a
/// <see cref="Message"/>. UI thread only.
/// </summary>
public sealed class DefaultAppViewModel : ViewModelBase, IDisposable
{
    private readonly Func<CancellationToken, Task<DefaultAppResult>> _makeDefault;
    private readonly Action _close;
    private readonly Action _never;
    private readonly CancellationTokenSource _cancellation = new();
    private string? _message;
    private bool _isFailed;
    private bool _isBusy;
    private bool _isClosed;

    /// <summary>Creates the view model of a card.</summary>
    /// <param name="makeDefault">Asks the system to make Pagewing the default app.</param>
    /// <param name="close">Removes the card (nothing is saved).</param>
    /// <param name="never">Saves "Don't ask again" (never throws) and removes the card.</param>
    public DefaultAppViewModel(Func<CancellationToken, Task<DefaultAppResult>> makeDefault, Action close, Action never)
    {
        ArgumentNullException.ThrowIfNull(makeDefault);
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(never);
        _makeDefault = makeDefault;
        _close = close;
        _never = never;
        SetDefaultCommand = new RelayCommand(() => _ = SetDefaultAsync(), () => !_isBusy && !_isClosed && _message != Strings.DefaultAppChooseHint);
        LaterCommand = new RelayCommand(Close);
        NeverCommand = new RelayCommand(() =>
        {
            Stop();
            _never();
        });
    }

    /// <summary>"Set as default": asks the system.</summary>
    public RelayCommand SetDefaultCommand { get; }

    /// <summary>"Later" and Esc: closes the card for this session, saves nothing.</summary>
    public ICommand LaterCommand { get; }

    /// <summary>"Don't ask again": saves the choice and closes the card.</summary>
    public ICommand NeverCommand { get; }

    /// <summary>Gets what the card says after an attempt (the hint for the system's list, or the failure), or <see langword="null"/>.</summary>
    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
                SetDefaultCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether <see cref="Message"/> is there.</summary>
    public bool HasMessage => _message is not null;

    /// <summary>Gets whether the attempt failed (the message then has the warning symbol).</summary>
    public bool IsFailed
    {
        get => _isFailed;
        private set => SetProperty(ref _isFailed, value);
    }

    /// <summary>Stops a running attempt; called when the card goes.</summary>
    public void Dispose()
    {
        Stop();
        _cancellation.Dispose();
    }

    private void Stop()
    {
        _isClosed = true;
        _cancellation.Cancel();
    }

    private void Close()
    {
        Stop();
        _close();
    }

    private async Task SetDefaultAsync()
    {
        _isBusy = true;
        SetDefaultCommand.RaiseCanExecuteChanged();
        DefaultAppResult result;
        try
        {
            result = await _makeDefault(_cancellation.Token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception)
        {
            result = DefaultAppResult.Failed;
        }
        finally
        {
            _isBusy = false;
        }

        if (_isClosed)
        {
            return;
        }

        switch (result)
        {
            case DefaultAppResult.Done:
                Close();
                return;
            case DefaultAppResult.OpenedSystemSettings:
                IsFailed = false;
                Message = Strings.DefaultAppChooseHint;
                break;
            default:
                IsFailed = true;
                Message = Strings.DefaultAppFailed;
                break;
        }

        SetDefaultCommand.RaiseCanExecuteChanged();
    }
}
