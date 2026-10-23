namespace Pagewing.Core.Printing;

/// <summary>How a page is fitted onto the paper.</summary>
public enum PrintScaling
{
    /// <summary>Scale each page up or down to the printable area.</summary>
    FitToPaper,

    /// <summary>Print at 100 %; what does not fit the printable area is cut off.</summary>
    ActualSize,
}

/// <summary>Double-sided printing.</summary>
public enum PrintDuplex
{
    /// <summary>The setting of the printer.</summary>
    Default,

    /// <summary>One side only.</summary>
    Off,

    /// <summary>Both sides, flipped on the long edge.</summary>
    LongEdge,

    /// <summary>Both sides, flipped on the short edge.</summary>
    ShortEdge,
}

/// <summary>The orientation of the paper.</summary>
public enum PrintOrientation
{
    /// <summary>The setting of the printer.</summary>
    Default,

    /// <summary>Portrait.</summary>
    Portrait,

    /// <summary>Landscape.</summary>
    Landscape,
}

/// <summary>Which pages to print.</summary>
public enum PrintPageChoice
{
    /// <summary>Every page.</summary>
    All,

    /// <summary>Only the page the user is looking at.</summary>
    CurrentPage,

    /// <summary>The pages in <see cref="PrintSettings.Range"/>.</summary>
    Range,
}

/// <summary>What the user chose in the print dialog.</summary>
public sealed record PrintSettings
{
    /// <summary>The smallest number of copies.</summary>
    public const int MinCopies = 1;

    /// <summary>The largest number of copies.</summary>
    public const int MaxCopies = 999;

    /// <summary>Gets the name of the printer, or an empty string for the printer of the system.</summary>
    public string Printer { get; init; } = string.Empty;

    /// <summary>Gets which pages to print.</summary>
    public PrintPageChoice Pages { get; init; } = PrintPageChoice.All;

    /// <summary>Gets the pages for <see cref="PrintPageChoice.Range"/>, in the syntax of <see cref="Pdf.PageRange"/> (<c>1,3,5-7</c>).</summary>
    public string Range { get; init; } = string.Empty;

    /// <summary>Gets the number of copies, 1 to 999.</summary>
    public int Copies { get; init; } = 1;

    /// <summary>Gets whether copies of a multi-page range come out as complete sets (1 2 3, 1 2 3) rather than page by page (1 1, 2 2, 3 3).</summary>
    public bool Collate { get; init; } = true;

    /// <summary>Gets how a page is fitted onto the paper.</summary>
    public PrintScaling Scaling { get; init; } = PrintScaling.FitToPaper;

    /// <summary>Gets double-sided printing.</summary>
    public PrintDuplex Duplex { get; init; } = PrintDuplex.Default;

    /// <summary>Gets the orientation of the paper.</summary>
    public PrintOrientation Orientation { get; init; } = PrintOrientation.Default;

    /// <summary>Returns the settings with the number of copies limited and every undefined value replaced by its default.</summary>
    public PrintSettings Normalized() => this with
    {
        Printer = Printer ?? string.Empty,
        Range = Range ?? string.Empty,
        Copies = Math.Clamp(Copies, MinCopies, MaxCopies),
        Pages = Enum.IsDefined(Pages) ? Pages : PrintPageChoice.All,
        Scaling = Enum.IsDefined(Scaling) ? Scaling : PrintScaling.FitToPaper,
        Duplex = Enum.IsDefined(Duplex) ? Duplex : PrintDuplex.Default,
        Orientation = Enum.IsDefined(Orientation) ? Orientation : PrintOrientation.Default,
    };
}
