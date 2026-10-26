using Avalonia.Platform;
using Pagewing.Core.Annotations;

namespace Pagewing.App.Services;

/// <summary>
/// Hands the app's own Geist Regular (<c>Assets/Fonts</c>, the subset the UI uses) to the text boxes
/// (<see cref="TextBoxFont"/>), so the font exists once in the package. Only a delegate is set when the editor of a tab is
/// made; the file is read on the PDFium thread the first time a text box is created or changed, never in the startup path.
/// </summary>
internal static class TextBoxFontSource
{
    /// <summary>The font file in the app's resources.</summary>
    private static readonly Uri GeistRegular = new("avares://Pagewing/Assets/Fonts/Geist-Regular.ttf");

    /// <summary>Sets the source of the text box font (the first one set stays; cheap).</summary>
    public static void Register() => TextBoxFont.Use(Load);

    /// <summary>Reads the font file.</summary>
    /// <returns>The bytes of Geist Regular.</returns>
    public static byte[] Load()
    {
        using var stream = AssetLoader.Open(GeistRegular);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
