using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pagewing.App.Platform.Linux;

/// <summary><c>cups_option_t</c> from <c>cups/options.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CupsOption
{
    public byte* Name;
    public byte* Value;
}

/// <summary><c>cups_dest_t</c> from <c>cups/dest.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CupsDest
{
    public byte* Name;
    public byte* Instance;
    public int IsDefault;
    public int OptionCount;
    public CupsOption* Options;
}

/// <summary>
/// The few functions of libcups 2 that printing needs (<c>cups/cups.h</c>, <c>cups/dest.h</c>). The library is looked
/// for on first use, never at startup; <see cref="IsAvailable"/> says whether it is there. Only on Linux.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class CupsNative
{
    private const string Library = "libcups.so.2";

    private static readonly Lazy<bool> Available = new(Probe);

    /// <summary>Gets whether libcups can be loaded.</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary><c>cupsGetDests2</c>: lists the destinations; returns their number.</summary>
    [LibraryImport(Library)]
    public static partial int cupsGetDests2(nint http, CupsDest** dests);

    [LibraryImport(Library)]
    public static partial void cupsFreeDests(int count, CupsDest* dests);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial CupsDest* cupsGetNamedDest(nint http, string? name, string? instance);

    [LibraryImport(Library)]
    public static partial nint cupsCopyDestInfo(nint http, CupsDest* dest);

    [LibraryImport(Library)]
    public static partial void cupsFreeDestInfo(nint info);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsCheckDestSupported(nint http, CupsDest* dest, nint info, string option, string? value);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsAddOption(string name, string value, int count, CupsOption** options);

    [LibraryImport(Library)]
    public static partial void cupsFreeOptions(int count, CupsOption* options);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int cupsPrintFile2(nint http, string printer, string fileName, string title, int count, CupsOption* options);

    [LibraryImport(Library)]
    public static partial byte* cupsLastErrorString();

    private static bool Probe()
    {
        if (!NativeLibrary.TryLoad(Library, typeof(CupsNative).Assembly, null, out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }
}
