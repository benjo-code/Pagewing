using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Services;

/// <summary>
/// One drag of a tab out of a tab strip, from the moment it comes loose until it is dropped or cancelled. The
/// <see cref="Floating"/> window follows the pointer: a new window that took the tab, or, for the only tab of a window,
/// that window itself (<see cref="MovesWholeWindow"/>). The strip that leads the drag reports the pointer on the screen
/// (physical pixels). Every call after the drag ended, or after the floating window was closed, does nothing and never
/// throws.
/// </summary>
internal sealed class TabDrag
{
    private readonly Workspace _workspace;
    private readonly MainViewModel _source;
    private readonly TabViewModel _tab;
    private readonly int _originalIndex;
    private readonly PixelPoint _grab;
    private readonly PixelPoint _originalPosition;

    /// <param name="workspace">The workspace whose windows take part.</param>
    /// <param name="source">The window the tab came from.</param>
    /// <param name="tab">The dragged tab.</param>
    /// <param name="originalIndex">Where the tab was in <paramref name="source"/>.</param>
    /// <param name="floating">The window that follows the pointer.</param>
    /// <param name="grab">The offset of the pointer from the top left of <paramref name="floating"/>'s frame.</param>
    /// <param name="movesWholeWindow">Whether <paramref name="floating"/> is the source window with its only tab.</param>
    /// <param name="originalPosition">The position of the window before the drag (when it moves as a whole).</param>
    internal TabDrag(
        Workspace workspace, MainViewModel source, TabViewModel tab, int originalIndex, MainViewModel floating, PixelPoint grab, bool movesWholeWindow, PixelPoint originalPosition)
    {
        _workspace = workspace;
        _source = source;
        _tab = tab;
        _originalIndex = originalIndex;
        Floating = floating;
        _grab = grab;
        MovesWholeWindow = movesWholeWindow;
        _originalPosition = originalPosition;
    }

    /// <summary>Gets the window that follows the pointer.</summary>
    public MainViewModel Floating { get; }

    /// <summary>Gets whether the source window itself follows the pointer (it had only this tab).</summary>
    public bool MovesWholeWindow { get; }

    /// <summary>Gets whether the drag is over (dropped, cancelled, or its window closed).</summary>
    public bool IsEnded { get; private set; }

    /// <summary>The pointer moved: the floating window follows it.</summary>
    public void MoveTo(PixelPoint pointer)
    {
        if (IsLive(out var window))
        {
            window.Position = new PixelPoint(pointer.X - _grab.X, pointer.Y - _grab.Y);
        }
    }

    /// <summary>The button was released at <paramref name="pointer"/>: the floating window stays where it is and comes to the front.</summary>
    public void Complete(PixelPoint pointer)
    {
        if (!IsLive(out var window))
        {
            return;
        }

        MoveTo(pointer);
        End();
        window.Activate();
    }

    /// <summary>
    /// Esc: the tab goes back to its place in the source window, which gets it active again, and the window that took
    /// it closes; a window that moved as a whole goes back to where it was.
    /// </summary>
    public void Cancel()
    {
        if (!IsLive(out var window))
        {
            return;
        }

        if (MovesWholeWindow)
        {
            window.Position = _originalPosition;
        }
        else if (_workspace.Windows.Contains(_source))
        {
            Floating.DetachTab(_tab);
            _source.AttachTab(_tab, _originalIndex);

            // Not inside the event of the strip that leads the drag.
            Dispatcher.UIThread.Post(window.Close);
        }
        else
        {
            // The source window is gone: the tab stays with the window that has it.
            window.Activate();
        }

        End();
    }

    private bool IsLive(out Window window)
    {
        window = null!;
        if (IsEnded)
        {
            return false;
        }

        if (!_workspace.Windows.Contains(Floating))
        {
            End();
            return false;
        }

        window = _workspace.WindowOf(Floating);
        return true;
    }

    private void End()
    {
        IsEnded = true;
        _workspace.EndDrag(this);
    }
}
