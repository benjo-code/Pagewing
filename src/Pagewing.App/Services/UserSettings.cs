using Pagewing.Core.Settings;

namespace Pagewing.App.Services;

/// <summary>
/// The settings of the running app: what <see cref="Program.Main"/> read before Avalonia started, and every change
/// since. Used on the UI thread (after the start). Reading and writing never throw: a missing or broken file gives the
/// defaults, a failed write keeps the value in memory and sets <see cref="LastSaveFailed"/>.
/// </summary>
public sealed class UserSettings
{
    /// <summary>The name of the settings file in the config folder (<see cref="UserDataPaths.ConfigDirectory()"/>).</summary>
    public const string FileName = "settings.json";

    // Null for detached settings: they never touch a file.
    private readonly SettingsStore? _store;

    private UserSettings(SettingsStore? store, AppSettings current)
    {
        _store = store;
        Current = current;
    }

    /// <summary>Raised after every change (<see cref="Update"/>) with the new value, before it is written.</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>
    /// Raised after every change once the write is over (<see cref="LastSaveFailed"/> tells how it went); for detached
    /// settings right after <see cref="Changed"/>.
    /// </summary>
    public event Action? SaveCompleted;

    /// <summary>Gets the current settings, always normalized. Treat the instance as immutable.</summary>
    public AppSettings Current { get; private set; }

    /// <summary>Gets whether the last write failed (the value stays in memory; the next successful write clears it).</summary>
    public bool LastSaveFailed { get; private set; }

    /// <summary>Gets the settings file, or <see langword="null"/> for <see cref="Detached"/> settings.</summary>
    public string? FilePath => _store?.FilePath;

    /// <summary>
    /// Reads <see cref="FileName"/> in the config folder of the user (startup path). Without the file the defaults come
    /// back at once: <see cref="SettingsStore.Load"/> only parses JSON when there is a file.
    /// </summary>
    public static UserSettings Load() => Load(UserDataPaths.ConfigDirectory());

    /// <summary>Reads <see cref="FileName"/> in <paramref name="directory"/> (tests give a temporary folder).</summary>
    internal static UserSettings Load(string directory)
    {
        var store = new SettingsStore(Path.Combine(directory, FileName));
        return new UserSettings(store, store.Load());
    }

    /// <summary>Settings in memory only, starting with the defaults; they never read or write a file (tests, designer).</summary>
    public static UserSettings Detached() => new(null, AppSettings.Default);

    /// <summary>
    /// Settings in memory only that start with what <paramref name="file"/> holds (<see cref="SettingsStore.LoadReadOnly"/>):
    /// the file is only read, never written, and nothing else is touched. A missing or corrupt file gives the defaults.
    /// For measurement runs with <c>--measure-settings</c>.
    /// </summary>
    public static UserSettings DetachedFrom(string file) => new(null, new SettingsStore(file).LoadReadOnly());

    /// <summary>
    /// Changes the settings: normalizes the result of <paramref name="change"/>, announces it (<see cref="Changed"/>)
    /// and then writes it. A result equal to <see cref="Current"/> changes nothing and writes nothing.
    /// </summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Current).Normalized();
        if (next == Current)
        {
            return;
        }

        Current = next;
        Changed?.Invoke(next);
        if (_store is not null)
        {
            // Current, not next: a handler may have changed the settings again (and written them) meanwhile.
            LastSaveFailed = !_store.TrySave(Current);
        }

        SaveCompleted?.Invoke();
    }
}
