using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Pagewing.App.Resources;
using Pagewing.Core.Printing;

namespace Pagewing.App.Platform.Windows;

/// <summary><c>PRINTPAGERANGE</c> from <c>commdlg.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PrintPageRange
{
    public uint FromPage;
    public uint ToPage;
}

/// <summary><c>PRINTDLGEXW</c> from <c>commdlg.h</c> (64-bit layout, 136 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PrintDlgEx
{
    public uint StructSize;
    public nint Owner;
    public nint DevMode;
    public nint DevNames;
    public nint DeviceContext;
    public uint Flags;
    public uint Flags2;
    public uint ExclusionFlags;
    public uint PageRangeCount;
    public uint MaxPageRanges;
    public PrintPageRange* PageRanges;
    public uint MinPage;
    public uint MaxPage;
    public uint Copies;
    public nint Instance;
    public char* PrintTemplateName;
    public nint Callback;
    public uint PropertyPageCount;
    public nint* PropertyPages;
    public uint StartPage;
    public uint ResultAction;
}

/// <summary>Writes the page ranges of the Windows print dialog as the text <see cref="PrintSettings.Range"/> takes.</summary>
internal static class PrintRangeText
{
    /// <summary>Turns the page ranges of the dialog into the text <see cref="PrintSettings.Range"/> takes (<c>1-3,5</c>).</summary>
    public static string Format(ReadOnlySpan<PrintPageRange> ranges)
    {
        var text = new StringBuilder();
        foreach (var range in ranges)
        {
            if (range.FromPage == 0 || range.ToPage == 0)
            {
                continue;
            }

            var from = Math.Min(range.FromPage, range.ToPage);
            var to = Math.Max(range.FromPage, range.ToPage);
            if (text.Length > 0)
            {
                text.Append(',');
            }

            text.Append(from.ToString(CultureInfo.InvariantCulture));
            if (to > from)
            {
                text.Append('-').Append(to.ToString(CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }
}

/// <summary>Reads the page choice out of the flags and ranges that <c>PrintDlgExW</c> returns.</summary>
internal static class PrintDialogChoice
{
    internal const uint PageNumbers = 0x00000002;
    internal const uint CurrentPage = 0x00400000;

    /// <summary>
    /// Returns the pages to print. The dialog reports a single page number typed into "Pages" (3, 5, 3-3) as
    /// <c>PD_CURRENTPAGE</c> with that page in the first range (measured on Windows 11, 2026-10-23), and several ranges
    /// (2-4,7) as <c>PD_PAGENUMS</c>. So <c>PD_CURRENTPAGE</c> means the current page of the viewer only when the ranges do
    /// not name one valid page; otherwise the range counts (reading it as the current page printed page 112 for "3").
    /// </summary>
    /// <param name="flags">The <c>Flags</c> of the dialog after it returned.</param>
    /// <param name="ranges">The page ranges the dialog returned.</param>
    /// <param name="pageCount">The pages of the document.</param>
    /// <returns>The page choice and, for a range, the text <see cref="PrintSettings.Range"/> takes.</returns>
    public static (PrintPageChoice Pages, string Range) Read(uint flags, ReadOnlySpan<PrintPageRange> ranges, int pageCount)
    {
        var text = PrintRangeText.Format(ranges);
        if ((flags & CurrentPage) != 0)
        {
            return IsOneValidPage(ranges, pageCount) ? (PrintPageChoice.Range, text) : (PrintPageChoice.CurrentPage, string.Empty);
        }

        return (flags & PageNumbers) != 0 && text.Length > 0 ? (PrintPageChoice.Range, text) : (PrintPageChoice.All, string.Empty);
    }

    private static bool IsOneValidPage(ReadOnlySpan<PrintPageRange> ranges, int pageCount) =>
        ranges.Length == 1 && ranges[0].FromPage == ranges[0].ToPage && ranges[0].FromPage >= 1 && ranges[0].FromPage <= (uint)pageCount;
}

/// <summary>What the print dialog returned: the device context of the chosen printer, owned by the caller, and the choices.</summary>
/// <param name="DeviceContext">The <c>HDC</c> of the printer; the caller deletes it.</param>
/// <param name="Settings">What the user chose.</param>
internal sealed record PrintDialogResult(nint DeviceContext, PrintSettings Settings);

/// <summary>The common print dialog of Windows (<c>PrintDlgExW</c>). Windows only; call it on the UI thread.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class PrintDialogNative
{
    internal const uint ReturnDc = 0x00000100;
    internal const uint NoSelection = 0x00000004;
    internal const uint Collate = 0x00000010;
    internal const uint ResultPrint = 1;
    internal const uint StartPageGeneral = 0xFFFFFFFF;
    internal const int MaxRanges = 32;

    /// <summary>Shows the dialog.</summary>
    /// <param name="owner">The window that owns the dialog; must be a real window.</param>
    /// <param name="pageCount">The pages of the document.</param>
    /// <param name="defaults">What the dialog starts with (copies, collate).</param>
    /// <returns>The choices and the device context, or <see langword="null"/> when the user cancelled.</returns>
    /// <exception cref="PrintException">The dialog could not be shown.</exception>
    public static PrintDialogResult? Show(nint owner, int pageCount, PrintSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (owner == 0)
        {
            throw new PrintException(Strings.PrintErrorDialog);
        }

        var ranges = (PrintPageRange*)NativeMemory.AllocZeroed((nuint)(MaxRanges * sizeof(PrintPageRange)));
        var dialog = new PrintDlgEx
        {
            StructSize = (uint)sizeof(PrintDlgEx),
            Owner = owner,
            Flags = ReturnDc | NoSelection | (defaults.Collate ? Collate : 0),
            PageRangeCount = 1,
            MaxPageRanges = MaxRanges,
            PageRanges = ranges,
            MinPage = 1,
            MaxPage = (uint)pageCount,
            Copies = (uint)Math.Clamp(defaults.Copies, PrintSettings.MinCopies, PrintSettings.MaxCopies),
            StartPage = StartPageGeneral,
        };
        ranges[0] = new PrintPageRange { FromPage = 1, ToPage = (uint)pageCount };

        try
        {
            var result = PrintDlgExW(&dialog);
            if (result != 0)
            {
                throw new PrintException(Strings.PrintErrorDialog, Marshal.GetExceptionForHR(result) ?? new InvalidOperationException("PrintDlgEx failed."));
            }

            if (dialog.ResultAction != ResultPrint || dialog.DeviceContext == 0)
            {
                if (dialog.DeviceContext != 0)
                {
                    Gdi.DeleteDC(dialog.DeviceContext);
                }

                return null;
            }

            return new PrintDialogResult(dialog.DeviceContext, ReadSettings(in dialog, pageCount));
        }
        finally
        {
            NativeMemory.Free(ranges);
            if (dialog.DevMode != 0)
            {
                GlobalMemory.GlobalFree(dialog.DevMode);
            }

            if (dialog.DevNames != 0)
            {
                GlobalMemory.GlobalFree(dialog.DevNames);
            }
        }
    }

    private static PrintSettings ReadSettings(in PrintDlgEx dialog, int pageCount)
    {
        var (pages, range) = PrintDialogChoice.Read(
            dialog.Flags, new ReadOnlySpan<PrintPageRange>(dialog.PageRanges, (int)Math.Min(dialog.PageRangeCount, MaxRanges)), pageCount);

        var duplex = PrintDuplex.Default;
        var orientation = PrintOrientation.Default;
        if (dialog.DevMode != 0)
        {
            var bytes = ReadGlobal(dialog.DevMode);
            (duplex, orientation) = DevMode.Read(bytes);
        }

        var printer = dialog.DevNames != 0 ? DevMode.ReadPrinterName(ReadGlobal(dialog.DevNames)) : null;
        return new PrintSettings
        {
            Printer = printer ?? string.Empty,
            Pages = pages,
            Range = range,
            Copies = (int)Math.Min(dialog.Copies, PrintSettings.MaxCopies),
            Collate = (dialog.Flags & Collate) != 0,
            Duplex = duplex,
            Orientation = orientation,
        }.Normalized();
    }

    private static byte[] ReadGlobal(nint handle)
    {
        var pointer = GlobalMemory.GlobalLock(handle);
        if (pointer == 0)
        {
            return [];
        }

        try
        {
            var size = (int)Math.Min(GlobalMemory.GlobalSize(handle), 1 << 16);
            return new ReadOnlySpan<byte>((void*)pointer, size).ToArray();
        }
        finally
        {
            GlobalMemory.GlobalUnlock(handle);
        }
    }

    [LibraryImport("comdlg32.dll")]
    private static partial int PrintDlgExW(PrintDlgEx* dialog);
}
