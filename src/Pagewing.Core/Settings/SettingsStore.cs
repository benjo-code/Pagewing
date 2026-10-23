using System.Text.Json;

namespace Pagewing.Core.Settings;

/// <summary>
/// Reads and writes the settings file. Neither method throws: a failure to read gives the defaults, a failure to
/// write gives <c>false</c>, so the app never ends or blocks over its settings.
/// </summary>
public sealed class SettingsStore(string path)
{
    /// <summary>The settings file.</summary>
    public string FilePath { get; } = path;

    /// <summary>
    /// Loads the settings. A missing, empty or unreadable file gives <see cref="AppSettings.Default"/>; a corrupt
    /// file does too, after it has been copied once to <c>&lt;path&gt;.bad</c>. The result is always normalized.
    /// </summary>
    public AppSettings Load() => Load(keepBackup: true);

    /// <summary>
    /// Loads the settings like <see cref="Load()"/> but never writes: a corrupt file gives the defaults without a
    /// <c>&lt;path&gt;.bad</c> copy. For a file that is only input (the hidden switch <c>--measure-settings</c>).
    /// </summary>
    public AppSettings LoadReadOnly() => Load(keepBackup: false);

    private AppSettings Load(bool keepBackup)
    {
        var text = JsonFile.ReadText(FilePath);
        if (text is null)
        {
            return AppSettings.Default;
        }

        try
        {
            var settings = JsonSerializer.Deserialize(text, SettingsJsonContext.Default.AppSettings);
            if (settings is not null)
            {
                return settings.Normalized();
            }
        }
        catch (JsonException)
        {
            // Falls through to the backup below.
        }

        if (keepBackup)
        {
            JsonFile.KeepBackup(FilePath);
        }

        return AppSettings.Default;
    }

    /// <summary>
    /// Writes the settings: into <c>&lt;path&gt;.tmp</c> in the target folder first, then it replaces the file in one
    /// step. Returns <c>false</c> if that fails for a file system reason; the old file stays as it was.
    /// </summary>
    public bool TrySave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return JsonFile.TryWrite(FilePath, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
    }
}
