using System.Collections.ObjectModel;
using Pagewing.Core.Viewing;

namespace Pagewing.Core.Settings;

/// <summary>Where a document was last read: position, zoom and layout.</summary>
/// <param name="View">The scroll position.</param>
/// <param name="Zoom">The zoom factor, 1 is 100 %.</param>
/// <param name="ZoomMode">How the zoom is derived.</param>
/// <param name="Layout">The page layout.</param>
public sealed record RememberedView(ViewState View, double Zoom, ZoomMode ZoomMode, PageLayoutMode Layout)
{
    /// <summary>The smallest zoom a stored view may have.</summary>
    public const double MinZoom = 0.1;

    /// <summary>The largest zoom a stored view may have.</summary>
    public const double MaxZoom = 8;

    /// <summary>
    /// Whether every value is usable. A file edited by hand or written by another version may hold others. It does not
    /// know the document: the page may lie beyond its end and the fraction beyond its page; whoever restores the view
    /// limits both.
    /// </summary>
    public bool IsValid() =>
        View is not null
        && double.IsFinite(Zoom) && Zoom is >= MinZoom and <= MaxZoom
        && Enum.IsDefined(ZoomMode)
        && Enum.IsDefined(Layout)
        && View.Anchor.Page >= 0
        && double.IsFinite(View.Anchor.Fraction)
        && double.IsFinite(View.HorizontalFromCentre);
}

/// <summary>An entry of the list of recently opened files.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="LastOpened">When it was last opened.</param>
/// <param name="View">Where it was last read, or <c>null</c> if that is not known.</param>
public sealed record RecentFile(string Path, DateTimeOffset LastOpened, RememberedView? View);

/// <summary>
/// The list of recently opened files, newest first, at most <see cref="MaxEntries"/>. Paths are compared without
/// regard to case on Windows and with it elsewhere. Not thread-safe: use it from the UI thread only.
/// </summary>
public sealed class RecentFiles
{
    /// <summary>The most entries the list holds; the oldest fall away.</summary>
    public const int MaxEntries = 30;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly List<RecentFile> _items = [];

    /// <summary>
    /// Creates the list from entries ordered newest first. Entries with an empty path and later duplicates of a
    /// path are left out, and only the first <see cref="MaxEntries"/> are kept.
    /// </summary>
    public RecentFiles(IEnumerable<RecentFile>? items = null)
    {
        Items = new ReadOnlyCollection<RecentFile>(_items);
        if (items is null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (_items.Count >= MaxEntries)
            {
                break;
            }

            if (item is not null && !IsEmpty(item.Path) && IndexOf(item.Path) < 0)
            {
                _items.Add(item);
            }
        }
    }

    /// <summary>The entries, newest first.</summary>
    public IReadOnlyList<RecentFile> Items { get; }

    /// <summary>Moves the file to the top and keeps its stored view; an unknown file is added without a view.</summary>
    public void Touch(string path, DateTimeOffset now)
    {
        if (IsEmpty(path))
        {
            return;
        }

        var view = TryGet(path)?.View;
        Put(new RecentFile(path, now, view));
    }

    /// <summary>Stores where the file was read and moves it to the top; an unknown file is added.</summary>
    public void Remember(string path, RememberedView view, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (IsEmpty(path))
        {
            return;
        }

        Put(new RecentFile(path, now, view));
    }

    /// <summary>The entry for the path, or <c>null</c>.</summary>
    public RecentFile? TryGet(string path)
    {
        var index = IsEmpty(path) ? -1 : IndexOf(path);
        return index < 0 ? null : _items[index];
    }

    /// <summary>Removes the entry for the path; returns whether there was one.</summary>
    public bool Remove(string path)
    {
        var index = IsEmpty(path) ? -1 : IndexOf(path);
        if (index < 0)
        {
            return false;
        }

        _items.RemoveAt(index);
        return true;
    }

    /// <summary>Removes every entry.</summary>
    public void Clear() => _items.Clear();

    private static bool IsEmpty(string? path) => string.IsNullOrWhiteSpace(path);

    private void Put(RecentFile entry)
    {
        var index = IndexOf(entry.Path);
        if (index >= 0)
        {
            _items.RemoveAt(index);
        }

        _items.Insert(0, entry);
        if (_items.Count > MaxEntries)
        {
            _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
        }
    }

    private int IndexOf(string path) => _items.FindIndex(item => PathComparer.Equals(item.Path, path));
}
