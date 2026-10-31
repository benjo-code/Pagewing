using System.Buffers;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class RenderCacheTests
{
    private static TileKey Key(int page) => new(page, 1000, 0, 0);

    // An image whose pixel array has exactly the given number of bytes (at least 4, a multiple of 4).
    private static RenderedImage Image(int bytes) => new(1, 1, 4, new byte[bytes]);

    [Fact]
    public void EvictsLeastRecentlyUsedWhenOverBudget()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));
        cache.Add(Key(2), Image(40));
        cache.Add(Key(3), Image(40));

        Assert.False(cache.Contains(Key(1)));
        Assert.True(cache.Contains(Key(2)));
        Assert.True(cache.Contains(Key(3)));
        Assert.Equal(2, cache.Count);
        Assert.Equal(80, cache.UsedBytes);
    }

    [Fact]
    public void TryGetRefreshesRecency()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));
        cache.Add(Key(2), Image(40));

        Assert.True(cache.TryGet(Key(1), out _));
        cache.Add(Key(3), Image(40));

        Assert.True(cache.Contains(Key(1)));
        Assert.False(cache.Contains(Key(2)));
        Assert.True(cache.Contains(Key(3)));
    }

    [Fact]
    public void ContainsDoesNotRefreshRecency()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));
        cache.Add(Key(2), Image(40));

        Assert.True(cache.Contains(Key(1)));
        cache.Add(Key(3), Image(40));

        Assert.False(cache.Contains(Key(1)));
        Assert.True(cache.Contains(Key(2)));
        Assert.True(cache.Contains(Key(3)));
    }

    [Fact]
    public void ImageLargerThanTheBudgetIsNotStored()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));
        cache.Add(Key(2), Image(104));

        Assert.False(cache.Contains(Key(2)));
        Assert.True(cache.Contains(Key(1)));
        Assert.Equal(1, cache.Count);
        Assert.Equal(40, cache.UsedBytes);
    }

    [Fact]
    public void OversizedReplacementDropsTheStaleEntry()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));
        cache.Add(Key(1), Image(104));

        Assert.False(cache.TryGet(Key(1), out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.UsedBytes);
    }

    [Fact]
    public void CostUsesTheBackingArrayLength()
    {
        var backing = new byte[400];
        var slice = new RenderedImage(1, 1, 4, backing.AsMemory(8, 16));
        Assert.Equal(400, RenderCache.CostOf(slice));

        var cache = new RenderCache(budgetBytes: 1000);
        cache.Add(Key(1), slice);

        Assert.Equal(400, cache.UsedBytes);
    }

    [Fact]
    public void CostFallsBackToThePixelLengthWithoutAnArray()
    {
        var memory = new ReadOnlyMemory<byte>(new byte[8]);
        using var owner = new NonArrayMemoryManager(8);
        var image = new RenderedImage(1, 1, 4, owner.Memory);

        Assert.Equal(8, RenderCache.CostOf(image));
        Assert.Equal(8, RenderCache.CostOf(new RenderedImage(1, 1, 4, memory)));
    }

    [Fact]
    public void ReplacingAnEntryUpdatesUsedBytes()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));
        var replacement = Image(20);
        cache.Add(Key(1), replacement);

        Assert.Equal(1, cache.Count);
        Assert.Equal(20, cache.UsedBytes);
        Assert.True(cache.TryGet(Key(1), out var stored));
        Assert.Same(replacement, stored);
    }

    [Fact]
    public void ClearRemovesEverything()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));
        cache.Add(Key(2), Image(40));
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.UsedBytes);
        Assert.False(cache.Contains(Key(1)));
    }

    [Fact]
    public async Task ParallelAddAndGetKeepTheBudget()
    {
        const int keys = 64;
        var cache = new RenderCache(budgetBytes: 1000);
        var tasks = new Task[8];
        for (var t = 0; t < tasks.Length; t++)
        {
            var seed = t;
            tasks[t] = Task.Run(() =>
            {
                var random = new Random(seed);
                for (var i = 0; i < 10_000; i++)
                {
                    var key = Key(random.Next(keys));
                    if (random.Next(2) == 0)
                    {
                        cache.Add(key, Image(4 * random.Next(1, 60)));
                    }
                    else
                    {
                        cache.TryGet(key, out _);
                    }
                }
            }, TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(tasks);

        long sum = 0;
        var count = 0;
        for (var page = 0; page < keys; page++)
        {
            if (cache.Contains(Key(page)))
            {
                Assert.True(cache.TryGet(Key(page), out var image));
                sum += RenderCache.CostOf(image);
                count++;
            }
        }

        Assert.True(cache.UsedBytes <= cache.BudgetBytes);
        Assert.Equal(sum, cache.UsedBytes);
        Assert.Equal(count, cache.Count);
    }

    [Fact]
    public void ABudgetBelowOneIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderCache(0));
    }

    [Fact]
    public void SetBudgetEvictsLeastRecentlyUsedEntriesAtOnce()
    {
        var cache = new RenderCache(budgetBytes: 200);
        cache.Add(Key(1), Image(40));
        cache.Add(Key(2), Image(40));
        cache.Add(Key(3), Image(40));
        cache.Add(Key(4), Image(40));
        Assert.True(cache.TryGet(Key(1), out _));

        cache.SetBudget(90);

        Assert.Equal(90, cache.BudgetBytes);
        Assert.True(cache.UsedBytes <= 90);
        Assert.Equal(80, cache.UsedBytes);
        Assert.Equal(2, cache.Count);
        Assert.True(cache.Contains(Key(1)));
        Assert.True(cache.Contains(Key(4)));
        Assert.False(cache.Contains(Key(2)));
        Assert.False(cache.Contains(Key(3)));
    }

    [Fact]
    public void SetBudgetToALargerValueKeepsEverything()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));
        cache.Add(Key(2), Image(40));

        cache.SetBudget(1000);

        Assert.Equal(1000, cache.BudgetBytes);
        Assert.Equal(2, cache.Count);
        Assert.Equal(80, cache.UsedBytes);
    }

    [Fact]
    public void SetBudgetBelowOneThrows()
    {
        var cache = new RenderCache(budgetBytes: 100);
        cache.Add(Key(1), Image(40));

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SetBudget(0));

        Assert.Equal(100, cache.BudgetBytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void RemovePagesDropsOnlyThoseTiles()
    {
        var cache = new RenderCache(budgetBytes: 1000);
        TileKey[] pageOne = [new(1, TileKey.PreviewScaleKey, 0, 0), new(1, 1000, 0, 0), new(1, 2000, 1, 2)];
        TileKey[] pageTwo = [new(2, TileKey.PreviewScaleKey, 0, 0), new(2, 1000, 0, 0)];
        TileKey[] pageThree = [new(3, 1000, 3, 0)];
        foreach (var key in pageOne.Concat(pageTwo).Concat(pageThree))
        {
            cache.Add(key, Image(40));
        }

        cache.RemovePages(new HashSet<int> { 1, 3, 9 });

        Assert.All(pageOne.Concat(pageThree), key => Assert.False(cache.Contains(key)));
        Assert.All(pageTwo, key => Assert.True(cache.Contains(key)));
        Assert.Equal(2, cache.Count);
        Assert.Equal(80, cache.UsedBytes);
        Assert.Equal(1000, cache.BudgetBytes);

        // The order of use of the rest is intact: the least recently used of them goes first.
        cache.SetBudget(40);
        Assert.False(cache.Contains(pageTwo[0]));
        Assert.True(cache.Contains(pageTwo[1]));

        // An empty set changes nothing.
        cache.RemovePages(new HashSet<int>());
        Assert.Equal(1, cache.Count);
        Assert.Throws<ArgumentNullException>(() => cache.RemovePages(null!));
    }

    // Memory that is not exposed as an array, so CostOf has to use the pixel length.
    private sealed class NonArrayMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] bytes;

        public NonArrayMemoryManager(int length) => bytes = new byte[length];

        public override Span<byte> GetSpan() => bytes;

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
        }
    }
}
