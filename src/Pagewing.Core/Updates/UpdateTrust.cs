using System.Globalization;

namespace Pagewing.Core.Updates;

/// <summary>
/// What the program trusts for updates: the public keys and the addresses, compiled in with no way to override them
/// (no environment variable, no switch). This file is the release build. A test build swaps it for
/// <c>UpdateTrust.TestBuild.cs</c> only with the build property <c>PagewingUpdateTestBuild=true</c>, which no release
/// sets (Pagewing.Core.csproj; packaging/README.md, section "Update prüfen"); both files declare the same members.
/// </summary>
public static class UpdateTrust
{
    /// <summary>The identifier of the placeholder key below.</summary>
    public const string PlaceholderKeyId = "placeholder";

    /// <summary>
    /// The accepted public keys, any of which may have signed a manifest. The entry is the real public key of the
    /// project owner (<c>package.cs update-keygen</c>, packaging/README.md, section "Signaturschlüssel für Updates"); its
    /// private half is held by the owner and reaches the signing job only as the environment secret of
    /// <c>release-signing</c>. A new key is added next to this one first (key rotation, same section).
    /// </summary>
    public static IReadOnlyList<UpdateKey> Keys { get; } =
    [
        new UpdateKey(
            "release-1",
            Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAETdQ1ON9k3jJ5WUMMAv3TbQlQFVtd8kpm7X9Oq5PDqky9OvX7JQpBXIBIeyVH76/BYnlPsTXvIBm3lip/rHM5hw==")),
    ];

    /// <summary>The address of the newest stable release (GitHub lists no pre-releases or drafts there).</summary>
    public static string ReleasesLatestUrl => "https://api.github.com/repos/benjo-code/Pagewing/releases/latest";

    /// <summary>
    /// The page of the newest release, where a form that cannot update itself (the Debian package, the portable archive)
    /// gets the new version by hand; it is opened only through the confirmation card of external addresses.
    /// </summary>
    public static string ReleasesPageUrl => "https://github.com/benjo-code/Pagewing/releases/latest";

    /// <summary>The address the release files are below; a file is <c>&lt;base&gt;/&lt;tag&gt;/&lt;name&gt;</c>.</summary>
    public static string DownloadBaseUrl => "https://github.com/benjo-code/Pagewing/releases/download";

    /// <summary>
    /// What the about card shows after the version to mark a test build; <see langword="null"/> here, in the release
    /// build. The text lives only in the test build's file, so a release carries none of it.
    /// </summary>
    /// <param name="culture">The language of the user interface.</param>
    public static string? TestBuildNote(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return null;
    }
}
