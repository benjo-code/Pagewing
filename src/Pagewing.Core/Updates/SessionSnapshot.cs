using Pagewing.Core.Settings;

namespace Pagewing.Core.Updates;

/// <summary>A tab of a remembered session: its file and where it was read.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="View">Where the file was shown, or <see langword="null"/> when that is not known (the file was still loading, for example).</param>
public sealed record SessionTab(string Path, RememberedView? View);

/// <summary>A window of a remembered session: its tabs in their order and the active one.</summary>
/// <param name="Tabs">The tabs, from left to right.</param>
/// <param name="ActiveIndex">The position of the active tab in <paramref name="Tabs"/>.</param>
public sealed record SessionWindow(IReadOnlyList<SessionTab> Tabs, int ActiveIndex)
{
    /// <summary>Gets the active tab.</summary>
    public SessionTab Active => Tabs[ActiveIndex];
}

/// <summary>
/// The windows and tabs Pagewing had open when the updater ended it, so the new version opens them again
/// (<see cref="SessionStore"/>). Only the restart after an update restores a session; a normal start never does.
/// </summary>
/// <param name="Windows">The windows, the oldest first.</param>
public sealed record SessionSnapshot(IReadOnlyList<SessionWindow> Windows)
{
    /// <summary>The most windows a session brings back.</summary>
    public const int MaxWindows = 10;

    /// <summary>The most tabs a window of a session brings back (as many as one open of several files).</summary>
    public const int MaxTabsPerWindow = 20;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Gets a session without windows.</summary>
    public static SessionSnapshot Empty { get; } = new([]);

    /// <summary>
    /// Gets the session with only the files that <paramref name="exists"/> reports (moved or deleted files are dropped
    /// silently), cleaned like a loaded one (<see cref="Clean"/>). A window without a file left is dropped; when the active
    /// tab is dropped, its right neighbour (else its left one) becomes the active one, as when a tab is closed.
    /// </summary>
    public SessionSnapshot KeepFiles(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        return Clean(Windows.Select(window => ((IReadOnlyList<SessionTab?>?)window?.Tabs, window?.ActiveIndex ?? 0)), exists);
    }

    /// <summary>
    /// Makes a session that can be restored as it is from windows of any quality (a file edited by hand or written by
    /// another version): tabs without a path and later duplicates of a path in the same window are dropped, a view that is
    /// not usable (<see cref="RememberedView.IsValid"/>) is dropped from its tab, at most <see cref="MaxTabsPerWindow"/>
    /// tabs per window and <see cref="MaxWindows"/> windows are kept, windows without a tab are dropped, and the active
    /// index points at a kept tab (a dropped duplicate's first tab, a dropped tab's right neighbour, the nearest end of an
    /// index out of range). A window without a list of tabs counts as one without a tab.
    /// </summary>
    /// <param name="windows">The windows, the oldest first.</param>
    /// <param name="keep">Whether a file is kept (it still exists); all are when omitted.</param>
    internal static SessionSnapshot Clean(IEnumerable<(IReadOnlyList<SessionTab?>? Tabs, int ActiveIndex)> windows, Func<string, bool>? keep = null)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var result = new List<SessionWindow>();
        foreach (var (tabs, activeIndex) in windows)
        {
            if (result.Count >= MaxWindows)
            {
                break;
            }

            if (tabs is not null && CleanWindow(tabs, activeIndex, keep) is { } cleaned)
            {
                result.Add(cleaned);
            }
        }

        return new SessionSnapshot(result);
    }

    private static SessionWindow? CleanWindow(IReadOnlyList<SessionTab?> tabs, int activeIndex, Func<string, bool>? keep)
    {
        var active = tabs.Count == 0 ? 0 : Math.Clamp(activeIndex, 0, tabs.Count - 1);
        var kept = new List<SessionTab>();
        int? keptActive = null;
        int? keptBeforeActive = null;
        for (var i = 0; i < tabs.Count && kept.Count < MaxTabsPerWindow; i++)
        {
            if (i == active)
            {
                keptBeforeActive = kept.Count;
            }

            if (tabs[i] is not { } tab || string.IsNullOrWhiteSpace(tab.Path) || (keep is not null && !keep(tab.Path)))
            {
                continue;
            }

            var duplicate = kept.FindIndex(other => PathComparer.Equals(other.Path, tab.Path));
            if (duplicate >= 0)
            {
                if (i == active)
                {
                    keptActive = duplicate;
                }

                continue;
            }

            if (i == active)
            {
                keptActive = kept.Count;
            }

            kept.Add(tab.View is { } view && !view.IsValid() ? tab with { View = null } : tab);
        }

        if (kept.Count == 0)
        {
            return null;
        }

        return new SessionWindow(kept, keptActive ?? Math.Min(keptBeforeActive ?? kept.Count, kept.Count - 1));
    }
}
