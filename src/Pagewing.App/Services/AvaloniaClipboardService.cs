using System.Text;
using Avalonia.Controls;
using Avalonia.Input;

namespace Pagewing.App.Services;

/// <summary>
/// The clipboard of the platform, through the <see cref="TopLevel.Clipboard"/> of the window that holds a control. The
/// window is looked up when the data is put there, so creating the service touches nothing.
/// </summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    /// <summary>The name of the HTML format on Windows (CF_HTML).</summary>
    private const string WindowsHtmlFormat = "HTML Format";

    /// <summary>The MIME type of HTML on the clipboards of X11 and Wayland.</summary>
    private const string MimeHtmlFormat = "text/html";

    private readonly Control _owner;

    public AvaloniaClipboardService(Control owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public async Task SetTextAsync(string plainText, string? htmlFragment)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        if (TopLevel.GetTopLevel(_owner)?.Clipboard is not { } clipboard)
        {
            return;
        }

        // One item with both formats: the text and the HTML are two views of the same copy. The HTML goes as bytes
        // (a string format would be stored as UTF-16, which breaks the byte offsets of the Windows envelope).
        var item = new DataTransferItem();
        item.SetText(plainText);
        if (htmlFragment is not null)
        {
            if (OperatingSystem.IsWindows())
            {
                item.Set(DataFormat.CreateBytesPlatformFormat(WindowsHtmlFormat), ClipboardHtml.WrapForWindows(htmlFragment));
            }
            else
            {
                item.Set(DataFormat.CreateBytesPlatformFormat(MimeHtmlFormat), new UTF8Encoding(false).GetBytes(htmlFragment));
            }
        }

        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);

        // Keeps the data when the program ends.
        await clipboard.FlushAsync();
    }
}
