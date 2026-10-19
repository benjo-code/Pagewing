namespace Pagewing.App.ViewModels;

/// <summary>
/// What a window shows over its content (<see cref="MainViewModel.Overlay"/>): at most one of them at a time, each made
/// only when it is first shown.
/// </summary>
public enum OverlayKind
{
    /// <summary>Nothing lies over the window.</summary>
    None,

    /// <summary>The command palette (Ctrl+K).</summary>
    Palette,

    /// <summary>The settings card (Ctrl+Comma).</summary>
    Settings,

    /// <summary>The card with the keyboard shortcuts (F1).</summary>
    Help,
}
