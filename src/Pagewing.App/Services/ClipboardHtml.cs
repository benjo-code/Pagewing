using System.Globalization;
using System.Text;

namespace Pagewing.App.Services;

/// <summary>The "HTML Format" of the Windows clipboard (CF_HTML) around an HTML fragment.</summary>
internal static class ClipboardHtml
{
    private const string Prefix = "<html><body>\r\n<!--StartFragment-->";
    private const string Suffix = "<!--EndFragment-->\r\n</body></html>";

    /// <summary>
    /// Wraps a fragment in the envelope that Word, Outlook and browsers read: a header with the four offsets, then
    /// the document with the fragment between the two comments. The offsets count the bytes of the UTF-8 payload (not
    /// characters); it has neither a byte order mark nor a trailing NUL.
    /// </summary>
    /// <param name="fragment">The HTML fragment.</param>
    /// <returns>The payload for the clipboard format "HTML Format".</returns>
    public static byte[] WrapForWindows(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var prefix = encoding.GetBytes(Prefix);
        var body = encoding.GetBytes(fragment);
        var suffix = encoding.GetBytes(Suffix);

        // The numbers have a fixed width, so the header is as long with zeros as with the real offsets.
        var startHtml = encoding.GetByteCount(Header(0, 0, 0, 0));
        var startFragment = startHtml + prefix.Length;
        var endFragment = startFragment + body.Length;
        var endHtml = endFragment + suffix.Length;

        var header = encoding.GetBytes(Header(startHtml, endHtml, startFragment, endFragment));
        var result = new byte[endHtml];
        header.CopyTo(result, 0);
        prefix.CopyTo(result, startHtml);
        body.CopyTo(result, startFragment);
        suffix.CopyTo(result, endFragment);
        return result;
    }

    private static string Header(int startHtml, int endHtml, int startFragment, int endFragment)
    {
        var culture = CultureInfo.InvariantCulture;
        return string.Create(
            culture,
            $"Version:0.9\r\nStartHTML:{startHtml:D10}\r\nEndHTML:{endHtml:D10}\r\nStartFragment:{startFragment:D10}\r\nEndFragment:{endFragment:D10}\r\n");
    }
}
