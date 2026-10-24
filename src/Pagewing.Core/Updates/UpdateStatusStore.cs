using System.Text.Json;
using System.Text.Json.Serialization;
using Pagewing.Core.Settings;

namespace Pagewing.Core.Updates;

/// <summary>What Pagewing remembers about the update check between runs.</summary>
/// <param name="LastChecked">When the last successful check ended, or <see langword="null"/> if none has.</param>
/// <param name="AvailableVersion">The newer version found by that check, or <see langword="null"/> if there is none.</param>
public sealed record UpdateStatus(DateTimeOffset? LastChecked, string? AvailableVersion);

/// <summary>The shape of the file with the update status.</summary>
internal sealed class UpdateStatusDocument
{
    /// <summary>Version of the file format; a newer version than this app knows is tolerated.</summary>
    public int Version { get; set; } = 1;

    /// <summary>When the last successful check ended.</summary>
    public DateTimeOffset? LastChecked { get; set; }

    /// <summary>The newer version the last check found.</summary>
    public string? AvailableVersion { get; set; }
}

/// <summary>Source-generated JSON metadata for the update status (no reflection, Native AOT safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(UpdateStatusDocument))]
internal sealed partial class UpdateStatusJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads and writes the update status (<c>update.json</c> in the folder of <see cref="UserDataPaths.ConfigDirectory()"/>)
/// with the same rules as <see cref="RecentFilesStore"/>: neither method throws, a corrupt file is copied to
/// <c>&lt;path&gt;.bad</c> and gives an empty status, writing is atomic.
/// </summary>
public sealed class UpdateStatusStore(string path)
{
    /// <summary>The name of the status file in the config folder.</summary>
    public const string FileName = "update.json";

    /// <summary>The file with the status.</summary>
    public string FilePath { get; } = path;

    /// <summary>
    /// Loads the status. A missing, empty or unreadable file gives an empty status, a corrupt one too (after it has
    /// been copied once to <c>&lt;path&gt;.bad</c>). An available version that does not parse as an
    /// <see cref="UpdateVersion"/> is dropped.
    /// </summary>
    public UpdateStatus Load()
    {
        var text = JsonFile.ReadText(FilePath);
        if (text is null)
        {
            return new UpdateStatus(null, null);
        }

        try
        {
            var document = JsonSerializer.Deserialize(text, UpdateStatusJsonContext.Default.UpdateStatusDocument);
            if (document is not null)
            {
                var version = UpdateVersion.TryParse(document.AvailableVersion, out _) ? document.AvailableVersion : null;
                return new UpdateStatus(document.LastChecked, version);
            }
        }
        catch (JsonException)
        {
            // Falls through to the backup below.
        }

        JsonFile.KeepBackup(FilePath);
        return new UpdateStatus(null, null);
    }

    /// <summary>
    /// Writes the status atomically (see <see cref="SettingsStore.TrySave"/>). Returns <c>false</c> if that fails for a
    /// file system reason; the old file stays as it was.
    /// </summary>
    public bool TrySave(UpdateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var document = new UpdateStatusDocument { LastChecked = status.LastChecked, AvailableVersion = status.AvailableVersion };
        return JsonFile.TryWrite(FilePath, JsonSerializer.Serialize(document, UpdateStatusJsonContext.Default.UpdateStatusDocument));
    }
}
