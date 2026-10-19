namespace Pagewing.App.ViewModels;

/// <summary>What an entry of the command palette stands for; each kind has a group of its own (<see cref="PaletteGroup"/>).</summary>
public enum PaletteKind
{
    /// <summary>A recently opened file: opens it.</summary>
    Recent,

    /// <summary>"Go to page N" for a number typed into the palette.</summary>
    Page,

    /// <summary>An open tab of the window: activates it.</summary>
    Tab,

    /// <summary>An entry of the table of contents of the active tab: shows its page.</summary>
    Outline,

    /// <summary>A command of the catalog (<see cref="Commands.CommandCatalog"/>).</summary>
    Command,
}

/// <summary>An entry of the command palette.</summary>
/// <param name="Kind">What it stands for.</param>
/// <param name="Title">What the row shows first, in the language of the interface.</param>
/// <param name="Detail">What the row shows after it in <c>text.secondary</c> (folder, page), or <see langword="null"/>.</param>
/// <param name="Gesture">The shortcut as <see cref="Commands.GestureText"/> writes it, or <see langword="null"/>.</param>
/// <param name="Run">What choosing it does; the palette closes before.</param>
public sealed record PaletteItem(PaletteKind Kind, string Title, string? Detail, string? Gesture, Action Run)
{
    /// <summary>Gets what identifies the entry in tests: the command id, the path of a file or tab, or the page number.</summary>
    internal string? Key { get; init; }

    /// <summary>Gets the name for assistive technology: title, detail and shortcut, as far as there are any.</summary>
    public string AutomationName =>
        Detail is null
            ? Gesture is null ? Title : $"{Title}, {Gesture}"
            : Gesture is null ? $"{Title}, {Detail}" : $"{Title}, {Detail}, {Gesture}";
}

/// <summary>A group of the command palette with its heading and its entries (never empty).</summary>
/// <param name="Kind">The kind of its entries.</param>
/// <param name="Title">The heading, in the language of the interface.</param>
/// <param name="Items">The entries, best match first.</param>
public sealed record PaletteGroup(PaletteKind Kind, string Title, IReadOnlyList<PaletteItem> Items);
