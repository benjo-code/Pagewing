using System.Windows.Input;
using Avalonia.Input;
using Pagewing.App.Commands;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The keys of the window: one key binding for every gesture of the command catalog (<see cref="MainViewModel.Commands"/>),
/// made once when the window gets its view model, not on every key press. Key bindings of the window are matched from the
/// focused element upwards before any key event (also before tunnel handlers), so a field that needs one of these keys
/// claims it with a binding of its own. A command that cannot run lets its key go on to the focused element.
/// </summary>
public sealed partial class MainWindow
{
    private readonly List<KeyBinding> _commandBindings = [];

    private void AttachShortcuts(MainViewModel? next)
    {
        foreach (var binding in _commandBindings)
        {
            KeyBindings.Remove(binding);
        }

        _commandBindings.Clear();
        if (next is null)
        {
            return;
        }

        foreach (var command in next.Commands.All)
        {
            CatalogCommand? bound = null;
            foreach (var gesture in command.Gestures)
            {
                bound ??= new CatalogCommand(command);
                var binding = new KeyBinding { Gesture = gesture, Command = bound };
                _commandBindings.Add(binding);
                KeyBindings.Add(binding);
            }
        }
    }

    /// <summary>
    /// A command of the catalog as a key binding sees it. Whether it can run is asked at the moment of the key
    /// (<c>KeyBinding.TryHandle</c> calls <see cref="ICommand.CanExecute"/> and never listens to
    /// <see cref="ICommand.CanExecuteChanged"/>, Avalonia 12.1.4), so the command raises no change.
    /// </summary>
    private sealed class CatalogCommand(AppCommand command) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => command.CanExecute();

        public void Execute(object? parameter) => command.Execute();
    }
}
