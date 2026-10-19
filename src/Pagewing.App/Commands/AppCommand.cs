using Avalonia.Input;

namespace Pagewing.App.Commands;

/// <summary>
/// A command of the app as the catalog knows it (<see cref="CommandCatalog"/>): what the window binds to its keys, the
/// help card lists and the command palette offers.
/// </summary>
/// <param name="Id">Stable name, such as <c>view.zoomIn</c>.</param>
/// <param name="Title">The title in the language of the interface, read each time it is shown (never cached).</param>
/// <param name="Group">The group (one of <see cref="CommandCatalog.Groups"/>; <see cref="CommandCatalog.GroupTitle"/> names it).</param>
/// <param name="Gesture">The shortcut shown for the command, or <see langword="null"/> without one.</param>
/// <param name="CanExecute">Whether the command can run now; while it cannot, its keys go on to the focused element.</param>
/// <param name="Execute">Runs the command on what the window shows at that moment (its active tab).</param>
/// <param name="Listed">Whether the help card and the palette show it; a key of its own only (Esc of the link card) is not.</param>
public sealed record AppCommand(
    string Id,
    Func<string> Title,
    string Group,
    KeyGesture? Gesture,
    Func<bool> CanExecute,
    Action Execute,
    bool Listed = true)
{
    /// <summary>Further keys of the same command (number pad, US layouts), bound but not shown.</summary>
    public IReadOnlyList<KeyGesture> AlternativeGestures { get; init; } = [];

    /// <summary>
    /// Gets whether the command palette offers the command besides <see cref="CanExecute"/>, or <see langword="null"/>
    /// when it offers it whenever it can run. For commands whose key is taken in every state of the window although
    /// they do nothing there (Ctrl+F and Ctrl+Tab without a tab).
    /// </summary>
    public Func<bool>? Offered { get; init; }

    /// <summary>Gets <see cref="Gesture"/> and then <see cref="AlternativeGestures"/>: every key the window binds to the command.</summary>
    public IEnumerable<KeyGesture> Gestures => Gesture is null ? AlternativeGestures : [Gesture, .. AlternativeGestures];
}
