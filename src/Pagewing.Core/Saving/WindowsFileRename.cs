using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Pagewing.Core.Saving;

/// <summary>
/// The atomic replace of a file that other handles have open (Windows 10 1709 and later on NTFS): a rename with
/// <c>SetFileInformationByHandle(FileRenameInfoEx)</c> and <c>FILE_RENAME_FLAG_REPLACE_IF_EXISTS |
/// FILE_RENAME_FLAG_POSIX_SEMANTICS</c>. Existing handles to the replaced file stay valid and keep reading the old content.
/// <c>FILE_RENAME_FLAG_IGNORE_READONLY_ATTRIBUTE</c> is deliberately not set, so a read-only target is never replaced.
/// Values from <c>minwinbase.h</c> (<c>FILE_INFO_BY_HANDLE_CLASS</c>), <c>winbase.h</c> (<c>FILE_RENAME_INFO</c>) and
/// <c>ntifs.h</c> (<c>FILE_RENAME_INFORMATION</c> flags). Loaded only when a save replaces a file.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsFileRename
{
    /// <summary><c>ERROR_INVALID_FUNCTION</c>: the file system does not know the request.</summary>
    internal const int ErrorInvalidFunction = 1;

    /// <summary><c>ERROR_ACCESS_DENIED</c>.</summary>
    internal const int ErrorAccessDenied = 5;

    /// <summary><c>ERROR_NOT_SUPPORTED</c>: the file system has no POSIX rename (FAT, exFAT, some SMB servers).</summary>
    internal const int ErrorNotSupported = 50;

    /// <summary><c>ERROR_INVALID_PARAMETER</c>: Windows before 10 1709, or a file system that rejects the flags.</summary>
    internal const int ErrorInvalidParameter = 87;

    private const uint Delete = 0x0001_0000;
    private const uint Synchronize = 0x0010_0000;
    private const uint ShareReadWriteDelete = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceIfExists = 0x1;
    private const uint PosixSemantics = 0x2;

    /// <summary>Whether <paramref name="error"/> means the file system or Windows does not offer the POSIX rename.</summary>
    internal static bool IsUnsupported(int error) => error is ErrorInvalidFunction or ErrorNotSupported or ErrorInvalidParameter;

    /// <summary>Renames <paramref name="source"/> to <paramref name="target"/>, replacing it even while it is open.</summary>
    /// <param name="source">The full path of the file to rename; opened with delete access, sharing everything.</param>
    /// <param name="target">The full path of the file to replace, in the same volume.</param>
    /// <returns>0 on success, otherwise the Win32 error code.</returns>
    public static int ReplaceAtomically(string source, string target)
    {
        using var file = CreateFileW(LongPath(source), Delete | Synchronize, ShareReadWriteDelete, 0, OpenExisting, 0, 0);
        if (file.IsInvalid)
        {
            return Marshal.GetLastPInvokeError();
        }

        var info = RenameInfo(LongPath(target));
        return SetFileInformationByHandle(file, FileRenameInfoEx, info, (uint)info.Length) ? 0 : Marshal.GetLastPInvokeError();
    }

    /// <summary>Whether <paramref name="path"/> can be opened with the access <see cref="ReplaceAtomically"/> needs (DELETE), sharing everything.</summary>
    public static bool CanOpenForDelete(string path)
    {
        using var file = CreateFileW(LongPath(path), Delete | Synchronize, ShareReadWriteDelete, 0, OpenExisting, 0, 0);
        return !file.IsInvalid;
    }

    /// <summary>
    /// Builds <c>FILE_RENAME_INFO</c>: <c>DWORD Flags</c> at 0, <c>HANDLE RootDirectory</c> (null) at pointer size,
    /// <c>DWORD FileNameLength</c> (bytes, without terminator) after it, then the UTF-16 name. The buffer is larger than
    /// <c>sizeof(FILE_RENAME_INFO)</c> plus the name, as the API requires, and ends with a zero terminator.
    /// </summary>
    internal static byte[] RenameInfo(string target)
    {
        var lengthOffset = 2 * nint.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = checked(target.Length * sizeof(char));
        var info = new byte[nameOffset + nameBytes + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(info, ReplaceIfExists | PosixSemantics);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(lengthOffset), (uint)nameBytes);
        MemoryMarshal.AsBytes(target.AsSpan()).CopyTo(info.AsSpan(nameOffset));
        return info;
    }

    /// <summary>The <c>\\?\</c> form of a full path, so paths longer than <c>MAX_PATH</c> work.</summary>
    internal static string LongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, byte[] fileInformation, uint bufferSize);
}
