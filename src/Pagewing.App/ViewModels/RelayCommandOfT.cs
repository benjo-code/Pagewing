using System.Windows.Input;

namespace Pagewing.App.ViewModels;

/// <summary>
/// An <see cref="ICommand"/> that calls a delegate with its parameter; a parameter that is not a
/// <typeparamref name="T"/> (also <see langword="null"/> for a value type) does nothing. Used on the UI thread only.
/// </summary>
/// <typeparam name="T">The type of the command parameter.</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;

    public RelayCommand(Action<T> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
    }

    // Never raised: the command can always run for a parameter of the right type.
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => parameter is T;

    public void Execute(object? parameter)
    {
        if (parameter is T value)
        {
            _execute(value);
        }
    }
}
