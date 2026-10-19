namespace Pagewing.Core.Distribution;

/// <summary>Where the file type registration is written: the current user (<c>HKCU</c>) or all users (<c>HKLM</c>).</summary>
public enum RegistrationScope
{
    /// <summary>For the current user, needs no elevation.</summary>
    User,

    /// <summary>For all users, needs elevation.</summary>
    Machine,
}

/// <summary>A registry value to write. <paramref name="ValueName"/> <see langword="null"/> is the default value of the key.</summary>
/// <param name="KeyPath">The key, relative to the root (<c>HKCU</c> or <c>HKLM</c>).</param>
/// <param name="ValueName">The value name, or <see langword="null"/> for the default value.</param>
/// <param name="Value">The string value.</param>
public readonly record struct RegistryEntry(string KeyPath, string? ValueName, string Value);

/// <summary>A registry key or value to remove. <paramref name="ValueName"/> <see langword="null"/> removes the whole key.</summary>
/// <param name="KeyPath">The key, relative to the root.</param>
/// <param name="ValueName">The value to remove, or <see langword="null"/> for the whole key with its subkeys.</param>
public readonly record struct RegistryRemoval(string KeyPath, string? ValueName);

/// <summary>
/// What Pagewing registers in the Windows registry to be offered as an app for PDF files. Only the registration is
/// written (ProgId, "Open with", capabilities); the choice of the default app stays with the user, so nothing here
/// touches <c>UserChoice</c> or <c>FileExts</c>. Pure data, the writer is <c>WindowsFileTypeRegistry</c> in the app.
/// </summary>
public static class FileTypeRegistration
{
    /// <summary>The ProgId of PDF files opened with Pagewing.</summary>
    public const string ProgId = "Pagewing.PDF";

    private const string ProgIdKey = @"Software\Classes\" + ProgId;
    private const string ApplicationKey = @"Software\Classes\Applications\Pagewing.exe";
    private const string CapabilitiesKey = @"Software\Pagewing\Capabilities";
    private const string RegisteredApplicationsKey = @"Software\RegisteredApplications";
    private const string OpenWithProgidsKey = @"Software\Classes\.pdf\OpenWithProgids";

    /// <summary>Gets the values to write for the executable <paramref name="exePath"/>.</summary>
    public static IReadOnlyList<RegistryEntry> Entries(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var command = $"\"{exePath}\" \"%1\"";
        return
        [
            new(ProgIdKey, null, "Pagewing PDF Document"),
            new(ProgIdKey + @"\DefaultIcon", null, $"\"{exePath}\",0"),
            new(ProgIdKey + @"\shell\open\command", null, command),
            new(OpenWithProgidsKey, ProgId, string.Empty),
            new(ApplicationKey + @"\shell\open\command", null, command),
            new(CapabilitiesKey, "ApplicationName", "Pagewing"),
            new(CapabilitiesKey, "ApplicationDescription", "Fast, offline PDF viewer"),
            new(CapabilitiesKey + @"\FileAssociations", ".pdf", ProgId),
            new(RegisteredApplicationsKey, "Pagewing", CapabilitiesKey),
        ];
    }

    /// <summary>Gets what removes everything <see cref="Entries"/> wrote (and nothing else).</summary>
    public static IReadOnlyList<RegistryRemoval> Removals() =>
    [
        new(ProgIdKey, null),
        new(ApplicationKey, null),
        new(@"Software\Pagewing", null),
        new(OpenWithProgidsKey, ProgId),
        new(RegisteredApplicationsKey, "Pagewing"),
    ];
}
