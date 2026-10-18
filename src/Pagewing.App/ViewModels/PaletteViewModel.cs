using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.Core.Palette;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The command palette of a window (Ctrl+K, design system 7.6): an input and, below it, what matches it, in the groups
/// "Recently opened", "Pages" (a number goes to that page), "Tabs", "Contents" (the table of contents of the active
/// tab, once it is read) and "Commands" (the listed commands of the catalog that can run now, with their shortcuts), at
/// most <see cref="MaxRows"/> entries over all groups. Made by the window's view model when the palette is first shown
/// (<see cref="MainViewModel.Palette"/>), never in the startup path; it follows the window only while it is open.
/// UI thread only.
/// </summary>
public sealed class PaletteViewModel : ViewModelBase
{
    /// <summary>The most entries the palette shows over all groups.</summary>
    public const int MaxRows = 50;

    /// <summary>The most recently opened files it shows.</summary>
    public const int MaxRecent = 10;

    /// <summary>The most open tabs it shows.</summary>
    public const int MaxTabs = 10;

    /// <summary>How many entries Page Up and Page Down move the selection.</summary>
    internal const int PageRows = 8;

    private readonly MainViewModel _window;
    private string _query = string.Empty;
    private IReadOnlyList<PaletteGroup> _groups = [];
    private PaletteItem[] _items = [];
    private int _selectedIndex = -1;
    private bool _isOpen;
    private TabViewModel? _watchedTab;

    // The entries of the outline with a target, flattened in the order of the document, for the outline they came from.
    private IReadOnlyList<OutlineNodeViewModel>? _outlineSource;
    private OutlineEntry[] _outlineEntries = [];

    public PaletteViewModel(MainViewModel window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    /// <summary>Gets or sets what was typed; every change filters the entries anew and selects the first.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    /// <summary>Gets the groups that have entries, in their fixed order.</summary>
    public IReadOnlyList<PaletteGroup> Groups
    {
        get => _groups;
        private set => SetProperty(ref _groups, value);
    }

    /// <summary>Gets the entries of all groups in the order shown; <see cref="SelectedIndex"/> counts in it.</summary>
    public IReadOnlyList<PaletteItem> Items => _items;

    /// <summary>Gets or sets the chosen entry (index into <see cref="Items"/>), -1 without entries.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var index = _items.Length == 0 ? -1 : Math.Clamp(value, 0, _items.Length - 1);
            if (SetProperty(ref _selectedIndex, index))
            {
                OnPropertyChanged(nameof(SelectedItem));
            }
        }
    }

    /// <summary>Gets the chosen entry, or <see langword="null"/>.</summary>
    public PaletteItem? SelectedItem => _selectedIndex >= 0 ? _items[_selectedIndex] : null;

    /// <summary>Gets whether the palette is shown (between <see cref="OnOpened"/> and <see cref="OnClosed"/>).</summary>
    internal bool IsOpen => _isOpen;

    /// <summary>Down arrow: the next entry, after the last one the first.</summary>
    public void MoveNext()
    {
        if (_items.Length > 0)
        {
            SelectedIndex = (_selectedIndex + 1) % _items.Length;
        }
    }

    /// <summary>Up arrow: the previous entry, before the first one the last.</summary>
    public void MovePrevious()
    {
        if (_items.Length > 0)
        {
            SelectedIndex = _selectedIndex <= 0 ? _items.Length - 1 : _selectedIndex - 1;
        }
    }

    /// <summary>Page Down: <see cref="PageRows"/> entries further, at most to the last.</summary>
    public void PageDown() => SelectedIndex = _selectedIndex + PageRows;

    /// <summary>Page Up: <see cref="PageRows"/> entries back, at least to the first.</summary>
    public void PageUp() => SelectedIndex = Math.Max(_selectedIndex - PageRows, 0);

    /// <summary>Enter: closes the palette and does what the chosen entry stands for.</summary>
    public void Run() => Run(SelectedItem);

    /// <summary>A click on an entry: closes the palette and does what <paramref name="item"/> stands for.</summary>
    public void Run(PaletteItem? item)
    {
        if (item is null || !_isOpen)
        {
            return;
        }

        // First close (the focus goes back where it was), then run: a command that moves the focus keeps it.
        Close();
        item.Run();
    }

    /// <summary>Esc: closes the palette.</summary>
    public void Close()
    {
        if (_isOpen)
        {
            _window.HideOverlay();
        }
    }

    /// <summary>The window shows the palette: an empty input, the entries for it, and the window followed while it is open.</summary>
    internal void OnOpened()
    {
        if (_isOpen)
        {
            return;
        }

        _isOpen = true;
        _window.Tabs.CollectionChanged += OnTabsChanged;
        _window.PropertyChanged += OnWindowPropertyChanged;
        Watch(_window.ActiveTab);
        _query = string.Empty;
        OnPropertyChanged(nameof(Query));
        Refresh();
    }

    /// <summary>The window no longer shows the palette: it stops following the window.</summary>
    internal void OnClosed()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        _window.Tabs.CollectionChanged -= OnTabsChanged;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        Watch(null);
    }

    /// <summary>Computes the groups for <see cref="Query"/> and what the window shows now, and selects the first entry.</summary>
    internal void Refresh()
    {
        var query = _query.Trim();
        var tab = _window.ActiveTab;
        var recent = Recent(query);
        var page = Page(query, tab);
        var tabs = Tabs(query);
        var commands = Commands(query);

        // The table of contents only for a query (it can be huge) and with the rows the other groups leave.
        var budget = MaxRows - recent.Count - page.Count - tabs.Count;
        commands = commands.Count > budget ? commands.Take(Math.Max(budget, 0)).ToList() : commands;
        var outline = query.Length == 0 ? [] : Outline(query, tab, budget - commands.Count);

        var groups = new List<PaletteGroup>(5);
        Add(groups, PaletteKind.Recent, Strings.PaletteGroupRecent, recent);
        Add(groups, PaletteKind.Page, Strings.PaletteGroupPages, page);
        Add(groups, PaletteKind.Tab, Strings.PaletteGroupTabs, tabs);
        Add(groups, PaletteKind.Outline, Strings.PaletteGroupContents, outline);
        Add(groups, PaletteKind.Command, Strings.PaletteGroupCommands, commands);
        _items = groups.SelectMany(group => group.Items).ToArray();
        Groups = groups;
        OnPropertyChanged(nameof(Items));
        _selectedIndex = -2;
        SelectedIndex = 0;
    }

    private static void Add(List<PaletteGroup> groups, PaletteKind kind, string title, List<PaletteItem> items)
    {
        if (items.Count > 0)
        {
            groups.Add(new PaletteGroup(kind, title, items));
        }
    }

    /// <summary>The entries that match, best first (for an empty query in their own order), at most <paramref name="limit"/>.</summary>
    private static List<T> Best<T>(IEnumerable<T> candidates, string query, Func<T, int> score, int limit)
    {
        var matches = new List<(T Item, int Score, int Order)>();
        var order = 0;
        foreach (var candidate in candidates)
        {
            var value = score(candidate);
            if (value >= 0)
            {
                matches.Add((candidate, value, order));
            }

            order++;
        }

        if (query.Length > 0)
        {
            matches.Sort(static (a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Order.CompareTo(b.Order));
        }

        return matches.Take(Math.Max(limit, 0)).Select(match => match.Item).ToList();
    }

    private static string? FolderOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(path) is { Length: > 0 } folder ? folder : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The recently opened files (newest first), by file name; only the list, the disk is not asked.</summary>
    private List<PaletteItem> Recent(string query)
    {
        var files = _window.RecentFilesSource() ?? [];
        var shown = Best(files, query, file => PaletteMatcher.Score(query, Path.GetFileName(file.Path)), MaxRecent);
        return shown.Select(file =>
        {
            var path = file.Path;
            return new PaletteItem(PaletteKind.Recent, Path.GetFileName(path), FolderOf(path), null, () => _ = _window.OpenAsync(path)) { Key = path };
        }).ToList();
    }

    /// <summary>"Go to page N" when the query is a page number of the open document.</summary>
    private static List<PaletteItem> Page(string query, TabViewModel? tab)
    {
        if (tab is not { IsReady: true }
            || !int.TryParse(query, NumberStyles.None, CultureInfo.InvariantCulture, out var page)
            || page < 1 || page > tab.PageCount)
        {
            return [];
        }

        var title = string.Format(Strings.Culture, CompositeFormat.Parse(Strings.PaletteGoToPage), page);
        return [new PaletteItem(PaletteKind.Page, title, null, null, () => tab.GoToPage(page)) { Key = page.ToString(CultureInfo.InvariantCulture) }];
    }

    /// <summary>The open tabs by file name; without a query only when there is more than one.</summary>
    private List<PaletteItem> Tabs(string query)
    {
        if (query.Length == 0 && _window.Tabs.Count < 2)
        {
            return [];
        }

        var shown = Best(_window.Tabs, query, tab => PaletteMatcher.Score(query, tab.FileName ?? string.Empty), MaxTabs);
        return shown.Select(tab => new PaletteItem(
            PaletteKind.Tab,
            tab.FileName ?? Strings.AppName,
            tab.FullPath is { } path ? FolderOf(path) : null,
            null,
            () =>
            {
                if (_window.Tabs.IndexOf(tab) >= 0)
                {
                    _window.ActivateTabCommand.Execute(tab);
                }
            })
        { Key = tab.FullPath }).ToList();
    }

    /// <summary>
    /// The listed commands that can run now (and make sense here), by title or shortcut. The palette's own command is
    /// left out: the palette is open already.
    /// </summary>
    private List<PaletteItem> Commands(string query)
    {
        var candidates = _window.Commands.All.Where(command =>
            command.Listed
            && command.Id != "palette.show"
            && command.CanExecute()
            && (command.Offered?.Invoke() ?? true));
        var shown = Best(candidates, query, command => CommandScore(query, command), MaxRows);
        return shown.Select(command => new PaletteItem(
            PaletteKind.Command,
            command.Title(),
            null,
            command.Gesture is { } gesture ? GestureText.Of(gesture) : null,
            command.Execute)
        { Key = command.Id }).ToList();
    }

    private static int CommandScore(string query, AppCommand command)
    {
        var score = PaletteMatcher.Score(query, command.Title());
        return score < 0 && command.Gesture is { } gesture ? PaletteMatcher.Score(query, GestureText.Of(gesture)) : score;
    }

    /// <summary>
    /// The entries of the active tab's table of contents that match, at most <paramref name="limit"/>; only once it is
    /// read (the palette never waits for it). Only the shown entries become palette entries.
    /// </summary>
    private List<PaletteItem> Outline(string query, TabViewModel? tab, int limit)
    {
        if (limit <= 0 || tab is not { IsReady: true, OutlineState: OutlineState.Loaded, Outline: { } outline })
        {
            return [];
        }

        if (!ReferenceEquals(outline, _outlineSource))
        {
            _outlineSource = outline;
            _outlineEntries = Flatten(outline);
        }

        var shown = Best(_outlineEntries, query, entry => PaletteMatcher.Score(query, entry.Title), limit);
        var detail = CompositeFormat.Parse(Strings.PalettePageDetail);
        return shown.Select(entry =>
        {
            var page = entry.PageIndex + 1;
            return new PaletteItem(
                PaletteKind.Outline,
                entry.Title,
                string.Format(Strings.Culture, detail, page),
                null,
                () =>
                {
                    if (_window.Tabs.IndexOf(tab) >= 0)
                    {
                        tab.GoToPage(page);
                    }
                })
            { Key = page.ToString(CultureInfo.InvariantCulture) };
        }).ToList();
    }

    /// <summary>Every entry with a target, depth first in the order of the document, without making view models of the children.</summary>
    private static OutlineEntry[] Flatten(IReadOnlyList<OutlineNodeViewModel> roots)
    {
        var entries = new List<OutlineEntry>();
        var stack = new Stack<PdfOutlineItem>();
        for (var i = roots.Count - 1; i >= 0; i--)
        {
            stack.Push(roots[i].Item);
        }

        while (stack.Count > 0)
        {
            var item = stack.Pop();
            if (item.PageIndex is { } index)
            {
                entries.Add(new OutlineEntry(OutlineNodeViewModel.TitleOf(item), index));
            }

            for (var i = item.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(item.Children[i]);
            }
        }

        return [.. entries];
    }

    private void Watch(TabViewModel? tab)
    {
        if (ReferenceEquals(tab, _watchedTab))
        {
            return;
        }

        if (_watchedTab is not null)
        {
            _watchedTab.PropertyChanged -= OnTabPropertyChanged;
        }

        _watchedTab = tab;
        if (tab is not null)
        {
            tab.PropertyChanged += OnTabPropertyChanged;
            EnsureOutline(tab);
        }
    }

    /// <summary>
    /// Asks the tab to read its table of contents (at most once per document; the tab keeps it), but only after its
    /// first sharp page, so the palette never competes with the first sharp page or runs in the startup path.
    /// </summary>
    private static void EnsureOutline(TabViewModel tab)
    {
        if (tab is { IsReady: true, HasShownSharpPage: true, OutlineState: OutlineState.NotLoaded })
        {
            _ = tab.EnsureOutlineAsync();
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnWindowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            Watch(_window.ActiveTab);
            Refresh();
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TabViewModel tab)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(TabViewModel.HasShownSharpPage):
            case nameof(TabViewModel.IsReady):
                EnsureOutline(tab);
                Refresh();
                break;
            case nameof(TabViewModel.Outline):
            case nameof(TabViewModel.OutlineState):
            case nameof(TabViewModel.PageCount):
            case nameof(TabViewModel.FileName):
                Refresh();
                break;
        }
    }

    private readonly record struct OutlineEntry(string Title, int PageIndex);
}
