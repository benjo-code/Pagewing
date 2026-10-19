using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pagewing.Core.Settings;

/// <summary>The shape of the file with the recently opened files.</summary>
internal sealed class RecentFilesDocument
{
    /// <summary>Version of the file format; a newer version than this app knows is tolerated.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The entries, newest first.</summary>
    public List<RecentFile> Items { get; set; } = [];
}

/// <summary>Source-generated JSON metadata for the recent files (no reflection, Native AOT safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RecentFilesDocument))]
internal sealed partial class RecentFilesJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads and writes the list of recently opened files with the same rules as <see cref="SettingsStore"/>: neither
/// method throws, a corrupt file is copied to <c>&lt;path&gt;.bad</c> and gives an empty list, writing is atomic.
/// </summary>
public sealed class RecentFilesStore(string path)
{
    /// <summary>The file with the list.</summary>
    public string FilePath { get; } = path;

    /// <summary>
    /// Loads the list. A missing, empty or unreadable file gives an empty list, a corrupt one too (after it has been
    /// copied once to <c>&lt;path&gt;.bad</c>). The entries are cleaned up: empty and duplicate paths and everything
    /// beyond <see cref="RecentFiles.MaxEntries"/> are dropped, an entry whose view is not usable (zoom not finite or
    /// outside 0.1 to 8, undefined mode) keeps its place but loses the view.
    /// </summary>
    public RecentFiles Load()
    {
        var text = JsonFile.ReadText(FilePath);
        if (text is null)
        {
            return new RecentFiles();
        }

        try
        {
            var document = JsonSerializer.Deserialize(text, RecentFilesJsonContext.Default.RecentFilesDocument);
            if (document?.Items is { } items)
            {
                return new RecentFiles(items.Where(item => item is not null).Select(WithValidView));
            }
        }
        catch (JsonException)
        {
            // Falls through to the backup below.
        }

        JsonFile.KeepBackup(FilePath);
        return new RecentFiles();
    }

    /// <summary>
    /// Writes the list atomically (see <see cref="SettingsStore.TrySave"/>). Returns <c>false</c> if that fails for a
    /// file system reason; the old file stays as it was.
    /// </summary>
    public bool TrySave(RecentFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var document = new RecentFilesDocument { Items = [.. files.Items] };
        return JsonFile.TryWrite(FilePath, JsonSerializer.Serialize(document, RecentFilesJsonContext.Default.RecentFilesDocument));
    }

    private static RecentFile WithValidView(RecentFile item) =>
        item.View is { } view && !view.IsValid() ? item with { View = null } : item;
}
