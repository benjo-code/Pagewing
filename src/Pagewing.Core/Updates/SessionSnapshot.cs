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
    /// <summary>The most windows a session brings back (a bound against a hostile file; a real session stays far below it).</summary>
    public const int MaxWindows = 50;

    /// <summary>
    /// The most tabs a window of a session brings back (a bound against a hostile file). A window with more keeps a run of
    /// tabs around its active one, never without it.
    /// </summary>
    public const int MaxTabsPerWindow = 500;

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
    /// not usable (<see cref="RememberedView.IsValid"/>) is dropped from its tab, windows without a tab are dropped, and
    /// the active index points at a kept tab (a dropped duplicate's first tab, a dropped tab's right neighbour, the
    /// nearest end of an index out of range). A window with more than <see cref="MaxTabsPerWindow"/> tabs left keeps a run
    /// of that many around its active tab (centred on it where the window allows), so the active tab is never dropped; of
    /// more than <see cref="MaxWindows"/> windows the first (oldest) ones are kept. A window without a list of tabs counts
    /// as one without a tab.
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

        // The position of each kept path, for the duplicates (a hostile file may hold many tabs: no search per tab).
        var positions = new Dictionary<string, int>(PathComparer);
        int? keptActive = null;
        int? keptBeforeActive = null;
        for (var i = 0; i < tabs.Count; i++)
        {
            if (i == active)
            {
                keptBeforeActive = kept.Count;
            }

            if (tabs[i] is not { } tab || string.IsNullOrWhiteSpace(tab.Path) || (keep is not null && !keep(tab.Path)))
            {
                continue;
            }

            if (positions.TryGetValue(tab.Path, out var duplicate))
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

            positions.Add(tab.Path, kept.Count);
            kept.Add(tab.View is { } view && !view.IsValid() ? tab with { View = null } : tab);
        }

        if (kept.Count == 0)
        {
            return null;
        }

        var keptIndex = keptActive ?? Math.Min(keptBeforeActive ?? kept.Count, kept.Count - 1);
        if (kept.Count <= MaxTabsPerWindow)
        {
            return new SessionWindow(kept, keptIndex);
        }

        // Too many: a run around the active tab, centred on it unless an end of the window is nearer.
        var start = Math.Clamp(keptIndex - (MaxTabsPerWindow / 2), 0, kept.Count - MaxTabsPerWindow);
        return new SessionWindow(kept.GetRange(start, MaxTabsPerWindow), keptIndex - start);
    }
}
