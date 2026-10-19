using System.Runtime.Versioning;
using Microsoft.Win32;
using Pagewing.Core.Distribution;

namespace Pagewing.App.Platform.Windows;

/// <summary>
/// Writes and removes the file type registration (<see cref="FileTypeRegistration"/>) below a registry root:
/// <c>HKCU</c> or <c>HKLM</c> in production, a sandbox subkey in tests. Never touches <c>UserChoice</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsFileTypeRegistry
{
    private readonly RegistryKey _root;

    public WindowsFileTypeRegistry(RegistryKey root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
    }

    /// <summary>The registry for <paramref name="scope"/>: <c>HKCU</c> for the user, <c>HKLM</c> for all users.</summary>
    public static WindowsFileTypeRegistry For(RegistrationScope scope) =>
        new(scope == RegistrationScope.Machine ? Registry.LocalMachine : Registry.CurrentUser);

    /// <summary>Gets whether the open command of the ProgId and the "Open with" entry exist.</summary>
    public bool IsRegistered
    {
        get
        {
            using var command = _root.OpenSubKey(@"Software\Classes\" + FileTypeRegistration.ProgId + @"\shell\open\command");
            using var openWith = _root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids");
            return command?.GetValue(null) is string && openWith?.GetValue(FileTypeRegistration.ProgId) is not null;
        }
    }

    /// <summary>Gets whether every entry is present with exactly the value <see cref="Register"/> would write for <paramref name="exePath"/>.</summary>
    public bool IsCurrentFor(string exePath)
    {
        foreach (var entry in FileTypeRegistration.Entries(exePath))
        {
            using var key = _root.OpenSubKey(entry.KeyPath);
            if (key?.GetValue(entry.ValueName) is not string value || value != entry.Value)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether a registered open command exists below this root and points to a program other than
    /// <paramref name="exePath"/> (the folder was moved or replaced). A missing registration is not stale.
    /// </summary>
    public bool IsStaleFor(string exePath)
    {
        var expected = FileTypeRegistration.Entries(exePath)
            .First(e => e.KeyPath == @"Software\Classes\" + FileTypeRegistration.ProgId + @"\shell\open\command").Value;
        using var command = _root.OpenSubKey(@"Software\Classes\" + FileTypeRegistration.ProgId + @"\shell\open\command");
        return command?.GetValue(null) is string registered && registered != expected;
    }

    /// <summary>Writes every entry for <paramref name="exePath"/>; running it again just overwrites the same values.</summary>
    public void Register(string exePath)
    {
        foreach (var entry in FileTypeRegistration.Entries(exePath))
        {
            using var key = _root.CreateSubKey(entry.KeyPath, writable: true);
            key.SetValue(entry.ValueName, entry.Value, RegistryValueKind.String);
        }
    }

    /// <summary>Removes what <see cref="Register"/> wrote; whatever is missing is skipped.</summary>
    public void Unregister()
    {
        foreach (var removal in FileTypeRegistration.Removals())
        {
            if (removal.ValueName is null)
            {
                _root.DeleteSubKeyTree(removal.KeyPath, throwOnMissingSubKey: false);
                continue;
            }

            using var key = _root.OpenSubKey(removal.KeyPath, writable: true);
            key?.DeleteValue(removal.ValueName, throwOnMissingValue: false);
        }
    }
}
