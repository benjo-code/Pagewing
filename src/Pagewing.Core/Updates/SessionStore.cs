using System.Text.Json;
using System.Text.Json.Serialization;
using Pagewing.Core.Settings;

namespace Pagewing.Core.Updates;

/// <summary>The shape of the session file.</summary>
internal sealed class SessionDocument
{
    /// <summary>Version of the file format; a newer version than this app knows is tolerated.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The windows, the oldest first.</summary>
    public List<SessionWindowDocument?>? Windows { get; set; } = [];
}

/// <summary>A window in the session file.</summary>
internal sealed class SessionWindowDocument
{
    /// <summary>The position of the active tab.</summary>
    public int ActiveIndex { get; set; }

    /// <summary>The tabs, from left to right.</summary>
    public List<SessionTabDocument?>? Tabs { get; set; } = [];
}

/// <summary>A tab in the session file.</summary>
internal sealed class SessionTabDocument
{
    /// <summary>The full path of the file.</summary>
    public string? Path { get; set; }

    /// <summary>Where the file was shown.</summary>
    public RememberedView? View { get; set; }
}

/// <summary>Source-generated JSON metadata for the session (no reflection, Native AOT safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SessionDocument))]
internal sealed partial class SessionJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads, writes and deletes the session the updater keeps for the restart after an update (<c>session.json</c> in the
/// folder of <see cref="UserDataPaths.ConfigDirectory()"/>), with the same rules as <see cref="RecentFilesStore"/>: no
/// method throws, a corrupt file is copied to <c>&lt;path&gt;.bad</c> and gives an empty session, writing is atomic.
/// Only a start with <c>--restore-session</c> reads it, and then deletes it at once (it is used once).
/// </summary>
public sealed class SessionStore(string path)
{
    /// <summary>The name of the session file in the config folder.</summary>
    public const string FileName = "session.json";

    /// <summary>The file with the session.</summary>
    public string FilePath { get; } = path;

    /// <summary>
    /// Loads the session. A missing, empty or unreadable file gives an empty session, a corrupt one too (after it has been
    /// copied once to <c>&lt;path&gt;.bad</c>). The windows are cleaned (<see cref="SessionSnapshot.Clean"/>): empty
    /// paths, duplicates per window and unusable views are dropped, at most <see cref="SessionSnapshot.MaxTabsPerWindow"/>
    /// tabs per window and <see cref="SessionSnapshot.MaxWindows"/> windows are kept. Whether the files still exist is
    /// not checked here (<see cref="SessionSnapshot.KeepFiles"/>).
    /// </summary>
    public SessionSnapshot Load()
    {
        var text = JsonFile.ReadText(FilePath);
        if (text is null)
        {
            return SessionSnapshot.Empty;
        }

        try
        {
            var document = JsonSerializer.Deserialize(text, SessionJsonContext.Default.SessionDocument);
            if (document is not null)
            {
                return SessionSnapshot.Clean((document.Windows ?? []).Select(window => ((IReadOnlyList<SessionTab?>?)TabsOf(window), window?.ActiveIndex ?? 0)));
            }
        }
        catch (JsonException)
        {
            // Falls through to the backup below.
        }

        JsonFile.KeepBackup(FilePath);
        return SessionSnapshot.Empty;
    }

    /// <summary>
    /// Writes the session atomically (see <see cref="SettingsStore.TrySave"/>), cleaned like a loaded one, so nothing it
    /// holds can make the writing fail. Returns <c>false</c> if that fails for a file system reason; the old file stays as
    /// it was.
    /// </summary>
    public bool TrySave(SessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var cleaned = snapshot.KeepFiles(static _ => true);
        var document = new SessionDocument
        {
            Windows =
            [
                .. cleaned.Windows.Select(window => new SessionWindowDocument
                {
                    ActiveIndex = window.ActiveIndex,
                    Tabs = [.. window.Tabs.Select(tab => new SessionTabDocument { Path = tab.Path, View = tab.View })],
                }),
            ],
        };
        return JsonFile.TryWrite(FilePath, JsonSerializer.Serialize(document, SessionJsonContext.Default.SessionDocument));
    }

    /// <summary>Deletes the session file, so it is restored only once. A missing file or a failure is ignored.</summary>
    public void Delete() => JsonFile.DeleteQuietly(FilePath);

    private static List<SessionTab?>? TabsOf(SessionWindowDocument? window) =>
        window?.Tabs?.Select(tab => tab is { Path: { } path } ? new SessionTab(path, tab.View) : null).ToList();
}
