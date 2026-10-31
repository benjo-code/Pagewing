using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pagewing.Core.Saving;

/// <summary>
/// Tells whether two paths name the same file, by what the file system says about the file and not by how the paths are
/// spelled: Windows by volume serial number and file ID (<c>GetFileInformationByHandleEx(FileIdInfo)</c>, else
/// <c>GetFileInformationByHandle</c>), Linux by <c>st_dev</c> and <c>st_ino</c> (<c>statx</c>). Directory junctions, symbolic
/// links in any part of the path, <c>subst</c> drives, 8.3 names and hard links all give the same identity. Loaded only
/// when a command that writes a file checks its output against its input.
/// </summary>
public static partial class FileIdentity
{
    /// <summary>Compares the files at two paths (links are followed).</summary>
    /// <param name="pathA">The first path.</param>
    /// <param name="pathB">The second path.</param>
    /// <returns><see langword="true"/> for the same file (also two names of one file), <see langword="false"/> if they differ or one does not exist, <see langword="null"/> when the system cannot tell.</returns>
    public static bool? AreSameFile(string pathA, string pathB)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathA);
        ArgumentException.ThrowIfNullOrEmpty(pathB);
        var a = Path.GetFullPath(pathA);
        var b = Path.GetFullPath(pathB);
        if (!File.Exists(a) || !File.Exists(b))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                var first = WindowsId(a);
                var second = WindowsId(b);
                return first is null || second is null ? null : first.AsSpan().SequenceEqual(second);
            }

            if (OperatingSystem.IsLinux())
            {
                var first = LinuxId(a);
                var second = LinuxId(b);
                return first is null || second is null ? null : first.AsSpan().SequenceEqual(second);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    // ---- Windows ----

    private const uint ShareReadWriteDelete = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const int FileIdInfoClass = 18;
    private const int FileIdInfoLength = 24;
    private const int ByHandleInfoLength = 52;

    /// <summary>The volume and file ID as bytes, or <see langword="null"/> when the file cannot be asked.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[]? WindowsId(string fullPath)
    {
        // Access 0 asks for attributes only; no reparse flag, so a link is followed to the file behind it.
        using var file = Windows.CreateFileW(WindowsFileRename.LongPath(fullPath), 0, ShareReadWriteDelete, 0, OpenExisting, 0, 0);
        if (file.IsInvalid)
        {
            return null;
        }

        var extended = new byte[FileIdInfoLength];
        if (Windows.GetFileInformationByHandleEx(file, FileIdInfoClass, extended, (uint)extended.Length))
        {
            return extended;
        }

        // A file system without the 128-bit ID (older SMB servers): volume serial number and the 64-bit index.
        var info = new byte[ByHandleInfoLength];
        if (!Windows.GetFileInformationByHandle(file, info))
        {
            return null;
        }

        var id = new byte[12];
        info.AsSpan(28, 4).CopyTo(id);
        info.AsSpan(44, 8).CopyTo(id.AsSpan(4));
        return id;
    }

    // ---- Linux ----

    private const int AtFdCwd = -100;
    private const uint StatxBasicStats = 0x7ff;
    private const int StatxLength = 256;

    /// <summary>
    /// <c>stx_ino</c> (offset 32) and the device (<c>stx_dev_major</c> at 136, <c>stx_dev_minor</c> at 140) of
    /// <c>struct statx</c>, whose layout is the same on every architecture; <see langword="null"/> when the call fails.
    /// </summary>
    private static byte[]? LinuxId(string fullPath)
    {
        var buffer = new byte[StatxLength];
        if (Linux.Statx(AtFdCwd, fullPath, 0, StatxBasicStats, buffer) != 0)
        {
            return null;
        }

        var id = new byte[16];
        buffer.AsSpan(32, 8).CopyTo(id);
        buffer.AsSpan(136, 8).CopyTo(id.AsSpan(8));
        return id;
    }

    private static partial class Windows
    {
        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int fileInformationClass, [Out] byte[] fileInformation, uint bufferSize);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetFileInformationByHandle(SafeFileHandle file, [Out] byte[] fileInformation);
    }

    private static partial class Linux
    {
        [LibraryImport("libc.so.6", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int Statx(int directoryFd, string path, int flags, uint mask, [Out] byte[] buffer);
    }
}
