namespace Pagewing.Core.Pdf;

/// <summary>What the form scan knows about the pages of each field name.</summary>
/// <param name="Pages">The pages of each name, ascending; <see langword="null"/> when the scan went beyond the limits of the index
/// (a write then reads the names of every page with fields).</param>
/// <param name="IncompletePages">The pages whose names the scan could not read completely within its time budget, ascending; a
/// write reads their names again before it decides.</param>
internal sealed record FormNameIndex(IReadOnlyDictionary<string, int[]>? Pages, IReadOnlyList<int> IncompletePages);

/// <summary>
/// Builds a <see cref="FormNameIndex"/> page by page while the scan runs, applying its limits as the pages arrive: once the
/// names together are longer than the character limit, the (name, page) pairs exceed the entry limit or more pages than the
/// incomplete-page limit were cut short, everything is dropped and later pages add nothing. Memory stays bounded by the limits
/// plus the page being added.
/// </summary>
internal sealed class FormNameIndexBuilder(int characterLimit, int entryLimit, int incompletePageLimit)
{
    private Dictionary<string, List<int>>? pages = new(StringComparer.Ordinal);
    private List<int>? incomplete = [];

    /// <summary>Gets whether a limit was crossed: nothing is kept, and <see cref="Build"/> gives an index without pages.</summary>
    public bool Overflowed => pages is null;

    /// <summary>Gets the characters of the names kept (each name once).</summary>
    public long RetainedCharacters { get; private set; }

    /// <summary>Gets the (name, page) pairs kept.</summary>
    public long RetainedEntries { get; private set; }

    /// <summary>Adds the names of a page (each once), and whether the scan read all of them.</summary>
    public void Add(int pageIndex, IReadOnlyList<string> names, bool complete)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (pages is null || incomplete is null)
        {
            return;
        }

        if (!complete)
        {
            incomplete.Add(pageIndex);
            if (incomplete.Count > incompletePageLimit)
            {
                Drop();
                return;
            }
        }

        foreach (var name in names)
        {
            if (!pages.TryGetValue(name, out var list))
            {
                RetainedCharacters += name.Length;
                list = [];
                pages[name] = list;
            }

            list.Add(pageIndex);
            RetainedEntries++;
            if (RetainedCharacters > characterLimit || RetainedEntries > entryLimit)
            {
                Drop();
                return;
            }
        }
    }

    /// <summary>The index of the pages added so far.</summary>
    public FormNameIndex Build() =>
        pages is null || incomplete is null
            ? new FormNameIndex(null, [])
            : new FormNameIndex(
                pages.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal),
                [.. incomplete]);

    private void Drop()
    {
        pages = null;
        incomplete = null;
        RetainedCharacters = 0;
        RetainedEntries = 0;
    }
}
