using System.Text.Json;
using System.Text.Json.Serialization;
using Pagewing.Core.Settings;

namespace Pagewing.Core.Updates;

/// <summary>The names in the staging folder of an update (<c>&lt;config folder&gt;/updates/&lt;version&gt;/</c>).</summary>
public static class UpdateStaging
{
    /// <summary>The folder in the config folder that holds the staging folders.</summary>
    public const string DirectoryName = "updates";

    /// <summary>The signed manifest, byte for byte as received.</summary>
    public const string ManifestFileName = "update-manifest.json";

    /// <summary>The signature of the manifest, as received.</summary>
    public const string SignatureFileName = "update-manifest.json.sig";

    /// <summary>The request for the installer (<see cref="UpdateRequest"/>).</summary>
    public const string RequestFileName = "request.json";

    /// <summary>Gets the staging folder of <paramref name="version"/> in <paramref name="configDirectory"/>.</summary>
    public static string DirectoryFor(string configDirectory, UpdateVersion version) =>
        Path.Combine(configDirectory, DirectoryName, version.ToString());
}

/// <summary>
/// What the app leaves for the installer next to the verified file (<c>request.json</c>): which file of which version to
/// install, how the running program was installed, where it runs and where its progress window was, so the window of a
/// helper process can take its place. Nothing in it is a source of trust: the installer verifies the manifest and the file
/// again. Written with the source generator (Native AOT); a field without a value is left out.
/// </summary>
public sealed class UpdateRequest
{
    /// <summary>The version of the file format.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Gets or sets the version of the file format.</summary>
    public int Format { get; set; } = CurrentFormat;

    /// <summary>Gets or sets the version to install (<c>X.Y.Z</c>).</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the name of the verified file in the staging folder.</summary>
    public string? FileName { get; set; }

    /// <summary>Gets or sets how the running program was installed.</summary>
    public PackageForm Form { get; set; }

    /// <summary>Gets or sets the path of the running executable.</summary>
    public string? ExePath { get; set; }

    /// <summary>Gets or sets the folder the program is installed in (for an AppImage, the folder of the AppImage).</summary>
    public string? TargetDirectory { get; set; }

    /// <summary>Gets or sets the left edge of the progress window on the screen, in physical pixels.</summary>
    public int? Left { get; set; }

    /// <summary>Gets or sets the top edge of the progress window on the screen, in physical pixels.</summary>
    public int? Top { get; set; }

    /// <summary>Gets or sets the width of the progress window in device-independent pixels.</summary>
    public double? Width { get; set; }

    /// <summary>Gets or sets the height of the progress window in device-independent pixels.</summary>
    public double? Height { get; set; }

    /// <summary>
    /// Reads a request; <c>null</c> when the file is missing, unreadable or not a request (no version or file name).
    /// Never throws.
    /// </summary>
    public static UpdateRequest? Load(string path)
    {
        var text = JsonFile.ReadText(path);
        if (text is null)
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize(text, UpdateRequestJsonContext.Default.UpdateRequest);
            return request is { Version.Length: > 0, FileName.Length: > 0 } ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the request atomically; <c>false</c> if that fails for a file system reason (a full disk, no access).</summary>
    public bool TryWrite(string path) =>
        JsonFile.TryWrite(path, JsonSerializer.Serialize(this, UpdateRequestJsonContext.Default.UpdateRequest));
}

/// <summary>Source-generated JSON metadata for the request (no reflection, Native AOT safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(UpdateRequest))]
internal sealed partial class UpdateRequestJsonContext : JsonSerializerContext
{
}
