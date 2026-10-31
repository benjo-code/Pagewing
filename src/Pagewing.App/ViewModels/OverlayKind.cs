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

    /// <summary>The about card (command "About Pagewing"): version, licence, third-party notices.</summary>
    About,

    /// <summary>The print dialog (Ctrl+P), where the system has none of its own.</summary>
    Print,

    /// <summary>The dialog "New signature": draw one or import an image and keep it in the library.</summary>
    Signature,
}
