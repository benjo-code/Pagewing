using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pagewing.Core.Updates;

/// <summary>
/// The signed description of a release: its version and the installable files with size and SHA-256. Nothing in it
/// is trusted before <see cref="ManifestVerifier"/> has checked the signature over the raw bytes.
/// Additive changes (new file kinds, new fields) never bump <c>format</c>: a reader skips what it does not know, and
/// <see cref="ManifestVerifier"/> returns only the files of known kinds. Consumers parse <see cref="Version"/> with
/// <see cref="UpdateVersion.TryParse"/>, because the text may carry a <c>v</c> prefix.
/// </summary>
/// <param name="Format">Version of the manifest format; the only one this program understands is <see cref="CurrentFormat"/>.</param>
/// <param name="Version">The release version as text (<c>X.Y.Z</c>, an optional <c>v</c> in front).</param>
/// <param name="Files">The files of the release.</param>
public sealed record UpdateManifest(int Format, string Version, IReadOnlyList<UpdateFile> Files)
{
    /// <summary>The manifest format this program reads.</summary>
    public const int CurrentFormat = 1;

    /// <summary>The first file of the given <see cref="UpdateFile.Kind"/>, or <c>null</c>.</summary>
    public UpdateFile? FileOfKind(string kind) => Files.FirstOrDefault(file => string.Equals(file.Kind, kind, StringComparison.Ordinal));
}

/// <summary>One installable file of a release.</summary>
/// <param name="Kind">One of <see cref="UpdateFileKinds.All"/>.</param>
/// <param name="Name">The plain file name (no path), as it is named in the release.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Sha256">SHA-256 as 64 hexadecimal characters.</param>
public sealed record UpdateFile(string Kind, string Name, long Size, string Sha256);

/// <summary>The kinds of file a manifest may list.</summary>
public static class UpdateFileKinds
{
    /// <summary>The Windows installer.</summary>
    public const string WindowsSetup = "win-x64-setup";

    /// <summary>The portable Windows archive.</summary>
    public const string WindowsPortable = "win-x64-portable";

    /// <summary>The Linux AppImage.</summary>
    public const string LinuxAppImage = "linux-appimage";

    /// <summary>The Debian package.</summary>
    public const string LinuxDeb = "linux-deb";

    /// <summary>All known kinds.</summary>
    public static IReadOnlyList<string> All { get; } = [WindowsSetup, WindowsPortable, LinuxAppImage, LinuxDeb];
}

/// <summary>Source-generated JSON metadata for the update manifest (no reflection, Native AOT safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(UpdateManifest))]
internal sealed partial class UpdateManifestJsonContext : JsonSerializerContext
{
}
