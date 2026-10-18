using System.Collections;
using System.Collections.Specialized;

namespace Pagewing.Core.Viewing;

/// <summary>
/// An ordered list of tabs with one active tab. New tabs open to the right of the active one and become active;
/// closing the active tab activates its right neighbour, else its left one. Not thread-safe: use it from the UI
/// thread only. <see cref="CollectionChanged"/> reports <see cref="NotifyCollectionChangedAction.Add"/>,
/// <see cref="NotifyCollectionChangedAction.Remove"/> and <see cref="NotifyCollectionChangedAction.Move"/>, never
/// <see cref="NotifyCollectionChangedAction.Reset"/>. The list is fully updated before any event is raised.
/// </summary>
/// <typeparam name="T">The tab type, compared by reference.</typeparam>
public sealed class TabList<T> : IReadOnlyList<T>, INotifyCollectionChanged
    where T : class
{
    private readonly List<T> items = [];

    /// <summary>Raised after the list changed by an add, a remove or a move.</summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>Raised after <see cref="Active"/> has changed to a different tab (or to none).</summary>
    public event Action? ActiveChanged;

    /// <summary>Gets the active tab, or <see langword="null"/> when the list is empty.</summary>
    public T? Active { get; private set; }

    /// <summary>Gets the number of tabs.</summary>
    public int Count => items.Count;

    /// <summary>Gets the tab at the given position.</summary>
    /// <param name="index">The zero-based position.</param>
    public T this[int index] => items[index];

    /// <summary>Adds a tab to the right of the active tab (at the end when none is active) and activates it.</summary>
    /// <param name="item">The new tab.</param>
    public void Add(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        InsertAndActivate(item, Active is null ? items.Count : items.IndexOf(Active) + 1);
    }

    /// <summary>Adds a tab at a position (a tab dropped on the strip) and activates it.</summary>
    /// <param name="item">The new tab.</param>
    /// <param name="index">The position, limited to 0 up to <see cref="Count"/>.</param>
    public void Insert(T item, int index)
    {
        ArgumentNullException.ThrowIfNull(item);

        InsertAndActivate(item, Math.Clamp(index, 0, items.Count));
    }

    /// <summary>
    /// Adds a tab at the end of the list without changing the active tab, so no <see cref="ActiveChanged"/> is raised
    /// (files opened behind the tab the user is looking at). Several background tabs keep the order they were added in.
    /// Only when the list is empty, the tab becomes the active one, since a list with tabs always has an active tab.
    /// </summary>
    /// <param name="item">The new tab.</param>
    public void AddInBackground(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var index = items.Count;
        items.Add(item);
        var activated = Active is null;
        if (activated)
        {
            Active = item;
        }

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        if (activated)
        {
            ActiveChanged?.Invoke();
        }
    }

    /// <summary>Removes a tab. Closing the active tab activates its right neighbour, else its left one.</summary>
    /// <param name="item">The tab to close. An item that is not in the list changes nothing.</param>
    /// <returns>The active tab afterwards, or <see langword="null"/> when no tab is left.</returns>
    public T? Close(T item)
    {
        var index = items.IndexOf(item);
        if (index < 0)
        {
            return Active;
        }

        var wasActive = ReferenceEquals(Active, item);
        items.RemoveAt(index);
        if (wasActive)
        {
            Active = items.Count == 0 ? null : items[Math.Min(index, items.Count - 1)];
        }

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));
        if (wasActive)
        {
            ActiveChanged?.Invoke();
        }

        return Active;
    }

    /// <summary>Makes a tab the active one.</summary>
    /// <param name="item">The tab to activate.</param>
    /// <returns><see langword="true"/> when the tab is in the list (it is active afterwards), else <see langword="false"/>.</returns>
    public bool Activate(T item)
    {
        if (items.IndexOf(item) < 0)
        {
            return false;
        }

        if (!ReferenceEquals(Active, item))
        {
            Active = item;
            ActiveChanged?.Invoke();
        }

        return true;
    }

    /// <summary>Moves a tab to a new position; the active tab stays the same.</summary>
    /// <param name="item">The tab to move. An item that is not in the list changes nothing.</param>
    /// <param name="newIndex">The new position, limited to the valid range.</param>
    public void Move(T item, int newIndex)
    {
        var oldIndex = items.IndexOf(item);
        if (oldIndex < 0)
        {
            return;
        }

        newIndex = Math.Clamp(newIndex, 0, items.Count - 1);
        if (newIndex == oldIndex)
        {
            return;
        }

        items.RemoveAt(oldIndex);
        items.Insert(newIndex, item);

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Move, item, newIndex, oldIndex));
    }

    /// <summary>Activates the tab to the right of the active one, wrapping from the last to the first.</summary>
    public void ActivateNext() => ActivateRelative(1);

    /// <summary>Activates the tab to the left of the active one, wrapping from the first to the last.</summary>
    public void ActivatePrevious() => ActivateRelative(-1);

    /// <summary>Gets the position of a tab.</summary>
    /// <param name="item">The tab to look for.</param>
    /// <returns>The zero-based position, or -1 when the tab is not in the list.</returns>
    public int IndexOf(T item) => items.IndexOf(item);

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private void InsertAndActivate(T item, int index)
    {
        items.Insert(index, item);
        Active = item;

        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
        ActiveChanged?.Invoke();
    }

    private void ActivateRelative(int step)
    {
        if (items.Count < 2 || Active is null)
        {
            return;
        }

        var index = (items.IndexOf(Active) + step + items.Count) % items.Count;
        Activate(items[index]);
    }
}
