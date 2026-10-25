using System.Windows.Input;

namespace Pagewing.App.ViewModels;

/// <summary>
/// An <see cref="ICommand"/> that calls a delegate with its parameter; a parameter that is not a
/// <typeparamref name="T"/> (also <see langword="null"/> for a value type) does nothing. With a predicate it also runs only
/// for the parameters the predicate accepts (<see cref="RaiseCanExecuteChanged"/> tells the controls when its answer
/// may have changed). Used on the UI thread only.
/// </summary>
/// <typeparam name="T">The type of the command parameter.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    // Raised only by RaiseCanExecuteChanged: without a predicate the command can always run for a parameter of the right type.
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => parameter is T value && (_canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is T value && (_canExecute?.Invoke(value) ?? true))
        {
            _execute(value);
        }
    }

    /// <summary>Tells the bound controls to ask <see cref="CanExecute"/> again.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
