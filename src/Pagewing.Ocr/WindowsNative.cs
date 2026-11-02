using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pagewing.Ocr;

/// <summary>The two Windows calls the engine's folder handling needs.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNative
{
    /// <summary>The active code page of the process (65001 when the manifest asks for UTF-8).</summary>
    [LibraryImport("kernel32.dll")]
    public static partial uint GetACP();

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetShortPathName(string longPath, char* buffer, uint length);

    /// <summary>The 8.3 form of an existing path, or <see langword="null"/> when the file system has none.</summary>
    public static unsafe string? ShortPathOf(string path)
    {
        var size = GetShortPathName(path, null, 0);
        if (size == 0 || size > 32_768)
        {
            return null;
        }

        var buffer = new char[size];
        fixed (char* pointer = buffer)
        {
            var written = GetShortPathName(path, pointer, size);
            return written == 0 || written >= size ? null : new string(pointer, 0, (int)written);
        }
    }
}
