using System.ComponentModel;
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
    private readonly List<CatalogCommand> _catalogCommands = [];

    private void AttachShortcuts(MainViewModel? old, MainViewModel? next)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnShortcutsPropertyChanged;
        }

        foreach (var binding in _commandBindings)
        {
            KeyBindings.Remove(binding);
        }

        _commandBindings.Clear();
        _catalogCommands.Clear();
        if (next is null)
        {
            return;
        }

        next.PropertyChanged += OnShortcutsPropertyChanged;
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

            if (bound is not null)
            {
                _catalogCommands.Add(bound);
            }
        }
    }

    // Most commands act on the active tab: another one may change whether they can run (Esc: the link or the overlay).
    private void OnShortcutsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ActiveTab) or nameof(MainViewModel.PendingLink) or nameof(MainViewModel.Overlay))
        {
            foreach (var command in _catalogCommands)
            {
                command.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>A command of the catalog as a key binding sees it; whether it can run is asked at the moment of the key.</summary>
    private sealed class CatalogCommand(AppCommand command) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => command.CanExecute();

        public void Execute(object? parameter) => command.Execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
