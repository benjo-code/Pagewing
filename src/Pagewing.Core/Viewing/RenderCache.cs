using System.Runtime.InteropServices;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>
/// A least-recently-used cache for rendered tiles with a budget in bytes. Adding an image evicts the entries
/// that have gone unused longest until the cache fits the budget again. All members are thread-safe: the
/// render scheduler adds from a pool thread while the UI thread reads.
/// </summary>
public sealed class RenderCache
{
    /// <summary>The default budget: 256 MiB.</summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    private readonly object gate = new();
    private readonly Dictionary<TileKey, LinkedListNode<Entry>> entries = [];

    // Most recently used first; eviction takes from the end.
    private readonly LinkedList<Entry> order = new();
    private long usedBytes;

    // Written under the lock; volatile because the render scheduler reads BudgetBytes without it.
    private long budgetBytes;

    /// <summary>Creates an empty cache.</summary>
    /// <param name="budgetBytes">The most bytes the cache may hold.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budgetBytes"/> is less than 1.</exception>
    public RenderCache(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetBytes, 1L);
        this.budgetBytes = budgetBytes;
    }

    /// <summary>Gets the most bytes the cache may hold; see <see cref="SetBudget"/>.</summary>
    public long BudgetBytes => Volatile.Read(ref budgetBytes);

    /// <summary>Gets the bytes the cached images occupy, measured with <see cref="CostOf"/>.</summary>
    public long UsedBytes
    {
        get
        {
            lock (gate)
            {
                return usedBytes;
            }
        }
    }

    /// <summary>Gets the number of cached images.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>
    /// The memory a rendered image holds on to: the length of its backing array, because a tile that is a slice
    /// of a larger buffer (tiles with overlap) keeps the whole buffer alive; the pixel length otherwise.
    /// </summary>
    public static long CostOf(RenderedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return MemoryMarshal.TryGetArray(image.Pixels, out var segment) && segment.Array is { } array
            ? array.Length
            : image.Pixels.Length;
    }

    /// <summary>Looks up an image and marks it as the most recently used.</summary>
    public bool TryGet(TileKey key, out RenderedImage image)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var node))
            {
                order.Remove(node);
                order.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null!;
        return false;
    }

    /// <summary>Checks whether an image is cached without changing the order of use.</summary>
    public bool Contains(TileKey key)
    {
        lock (gate)
        {
            return entries.ContainsKey(key);
        }
    }

    /// <summary>
    /// Stores an image as the most recently used, replacing an earlier one under the same key, and evicts the
    /// least recently used entries until <see cref="UsedBytes"/> fits the budget. An image larger than the
    /// budget is not stored (an earlier image under the same key is dropped, as it is outdated).
    /// </summary>
    public void Add(TileKey key, RenderedImage image)
    {
        var cost = CostOf(image);
        lock (gate)
        {
            RemoveEntry(key);
            if (cost > budgetBytes)
            {
                return;
            }

            entries[key] = order.AddFirst(new Entry(key, image, cost));
            usedBytes += cost;
            Trim();
        }
    }

    /// <summary>
    /// Changes the budget. A smaller budget evicts the least recently used entries at once until the cache fits;
    /// a larger one keeps everything.
    /// </summary>
    /// <param name="budgetBytes">The most bytes the cache may hold.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="budgetBytes"/> is less than 1.</exception>
    public void SetBudget(long budgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budgetBytes, 1L);
        lock (gate)
        {
            Volatile.Write(ref this.budgetBytes, budgetBytes);
            Trim();
        }
    }

    /// <summary>Removes all images.</summary>
    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            order.Clear();
            usedBytes = 0;
        }
    }

    // Must run under the lock.
    private void Trim()
    {
        while (usedBytes > budgetBytes && order.Last is { } oldest)
        {
            RemoveEntry(oldest.Value.Key);
        }
    }

    // Must run under the lock.
    private void RemoveEntry(TileKey key)
    {
        if (entries.Remove(key, out var node))
        {
            order.Remove(node);
            usedBytes -= node.Value.Cost;
        }
    }

    private readonly record struct Entry(TileKey Key, RenderedImage Image, long Cost);
}
