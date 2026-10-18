namespace Pagewing.App.Services;

/// <summary>Puts text on the system clipboard (the clipboard of the window; a fake in tests).</summary>
public interface IClipboardService
{
    /// <summary>Puts plain text and, when given, an HTML fragment on the clipboard.</summary>
    /// <param name="plainText">The text for programs that take no formatting.</param>
    /// <param name="htmlFragment">An HTML fragment (no html/body) for programs that paste formatted text, or <see langword="null"/>.</param>
    /// <returns>A task that ends when the clipboard holds the data (it stays after the program ends).</returns>
    /// <exception cref="IOException">The clipboard is locked by another program.</exception>
    Task SetTextAsync(string plainText, string? htmlFragment);
}
