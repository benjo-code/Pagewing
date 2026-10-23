namespace Pagewing.Core.Updates;

/// <summary>
/// What the program trusts for updates: the public keys and the addresses, compiled in with no way to override them
/// in a shipped build (no environment variable, no switch). A test build gets its own values later (Milestone 8.1,
/// build-time property), never the release build.
/// </summary>
public static class UpdateTrust
{
    /// <summary>The identifier of the placeholder key below.</summary>
    public const string PlaceholderKeyId = "placeholder";

    /// <summary>
    /// The accepted public keys, any of which may have signed a manifest. The one entry is a PLACEHOLDER generated at
    /// design time (its private half was discarded): it is a valid P-256 key but no release can be signed with it.
    /// The real public key replaces it with the release pipeline (Milestone 8.1, signing job).
    /// </summary>
    public static IReadOnlyList<UpdateKey> Keys { get; } =
    [
        new UpdateKey(
            PlaceholderKeyId,
            Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfEyCOMU4IJ14osOpBY3eFP4/fxK726BrpSdQoUOGeMJ+2DsQaDm4maWvFaZ22iI1oti6dIVOk7thJ8J5Lkp59Q==")),
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
}
