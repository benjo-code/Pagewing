namespace Pagewing.Core.Redaction;

/// <summary>
/// The places where texts were found in one coordinate space (the file, or one unpacked stream). Several views find the same
/// place (the raw bytes, the decoded string, the array of a <c>TJ</c>, the text object); they are collapsed here, so one place is
/// counted once: matches are sorted and a match that starts inside the one before is dropped (the same rule as a search).
/// </summary>
internal sealed class IntervalStore(int termCount)
{
    /// <summary>The most places kept for one text in one space; further matches are only counted.</summary>
    public const int MaxKept = 1_000_000;

    private List<(long Start, long End)>[]? lists;
    private long[]? overflow;

    /// <summary>Gets whether matches were counted without their place.</summary>
    public bool Overflowed { get; private set; }

    /// <summary>Adds a match.</summary>
    /// <param name="term">The index of the text.</param>
    /// <param name="start">The offset of the first byte.</param>
    /// <param name="end">The offset after the last byte.</param>
    public void Add(int term, long start, long end)
    {
        lists ??= new List<(long, long)>[termCount];
        var list = lists[term] ??= [];
        if (list.Count >= MaxKept)
        {
            overflow ??= new long[termCount];
            overflow[term]++;
            Overflowed = true;
            return;
        }

        list.Add((start, end));
    }

    /// <summary>Adds the distinct places to <paramref name="counts"/> and empties the store.</summary>
    /// <param name="counts">The counts per text.</param>
    public void FlushInto(long[] counts)
    {
        if (lists is not null)
        {
            for (var t = 0; t < lists.Length; t++)
            {
                var list = lists[t];
                if (list is null || list.Count == 0)
                {
                    continue;
                }

                list.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
                var lastEnd = long.MinValue;
                foreach (var (start, end) in list)
                {
                    if (start >= lastEnd)
                    {
                        counts[t]++;
                        lastEnd = end;
                    }
                }

                list.Clear();
            }
        }

        if (overflow is not null)
        {
            for (var t = 0; t < overflow.Length; t++)
            {
                counts[t] += overflow[t];
                overflow[t] = 0;
            }
        }
    }
}

/// <summary>Finds the texts in bytes as they are (a chunk at a time, the state carried on).</summary>
internal sealed class RawView(TermAutomaton automaton, IntervalStore store)
{
    private int state;

    /// <summary>Forgets a partial match (the data is not contiguous with what was fed before).</summary>
    public void Reset() => state = 0;

    /// <summary>Searches a chunk.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">The offset of the first byte in the coordinate space of the store.</param>
    public void Feed(ReadOnlySpan<byte> data, long offset)
    {
        var s = state;
        var i = 0;
        while (i < data.Length)
        {
            if (s == 0)
            {
                var jump = data[i..].IndexOfAny(automaton.StartBytes);
                if (jump < 0)
                {
                    break;
                }

                i += jump;
            }

            s = automaton.Next(s, data[i]);
            if (automaton.HasOutput(s))
            {
                for (var node = automaton.FirstOutput(s); node != 0; node = automaton.NextOutput(node))
                {
                    foreach (var pattern in automaton.PatternsAt(node))
                    {
                        var end = offset + i + 1;
                        store.Add(automaton.TermOf(pattern), end - automaton.LengthOf(pattern), end);
                    }
                }
            }

            i++;
        }

        state = s;
    }
}

/// <summary>
/// A run of decoded string bytes (one string, the strings of an array, or the strings of a text object): every byte comes with the
/// offset of the source it was decoded from, so a match is placed where its first and last byte were written.
/// </summary>
internal sealed class StringChannel
{
    private readonly TermAutomaton automaton;
    private readonly IntervalStore store;
    private readonly long[] ring;
    private readonly int mask;
    private int state;
    private long count;

    public StringChannel(TermAutomaton automaton, IntervalStore store)
    {
        this.automaton = automaton;
        this.store = store;
        var size = 16;
        while (size <= automaton.MaxPatternLength)
        {
            size <<= 1;
        }

        ring = new long[size];
        mask = size - 1;
    }

    public void Reset()
    {
        state = 0;
        count = 0;
    }

    public void Push(byte value, long offset)
    {
        ring[count & mask] = offset;
        count++;
        state = automaton.Next(state, value);
        if (!automaton.HasOutput(state))
        {
            return;
        }

        for (var node = automaton.FirstOutput(state); node != 0; node = automaton.NextOutput(node))
        {
            foreach (var pattern in automaton.PatternsAt(node))
            {
                var length = automaton.LengthOf(pattern);
                if (count >= length)
                {
                    store.Add(automaton.TermOf(pattern), ring[(count - length) & mask], ring[(count - 1) & mask] + 1);
                }
            }
        }
    }
}

/// <summary>Counts the names that tell what a cleanup removes, and whether the file is encrypted.</summary>
internal sealed class NameTally
{
    public int MetadataKeys { get; private set; }

    public int JavaScriptActions { get; private set; }

    public int EmbeddedFiles { get; private set; }

    public int AttachmentAnnotations { get; private set; }

    public int OutlineItems { get; private set; }

    public int PageLabelKeys { get; private set; }

    public bool EncryptSeen { get; private set; }

    /// <summary>Counts a name.</summary>
    /// <param name="name">The name without its slash, <c>#xx</c> escapes resolved.</param>
    /// <param name="valueOfType">Whether the name is the value of a <c>/Type</c> entry.</param>
    public void OnName(ReadOnlySpan<byte> name, bool valueOfType)
    {
        switch (name.Length)
        {
            case 2 when name.SequenceEqual("JS"u8):
                JavaScriptActions++;
                break;
            case 7 when name.SequenceEqual("Encrypt"u8):
                EncryptSeen = true;
                break;
            case 8 when name.SequenceEqual("Metadata"u8) && !valueOfType:
                MetadataKeys++;
                break;
            case 10 when name.SequenceEqual("PageLabels"u8):
                PageLabelKeys++;
                break;
            case 12 when valueOfType && name.SequenceEqual("EmbeddedFile"u8):
                EmbeddedFiles++;
                break;
            case 14 when name.SequenceEqual("FileAttachment"u8):
                // /Subtype /FileAttachment: a paperclip annotation; its file comes with the page and is not cleaned.
                AttachmentAnnotations++;
                break;
            default:
                break;
        }
    }

    public void OnOutlineItem() => OutlineItems++;
}

/// <summary>Collects the notes of a scan, one entry per kind and detail.</summary>
internal sealed class NoteBuilder
{
    private const int MaxEntries = 64;

    private readonly Dictionary<(RawScanNoteKind Kind, string Detail), int> index = [];
    private readonly List<RawScanNote> notes = [];

    public int StreamsScanned { get; set; }

    public long InflatedBytes { get; set; }

    public void Add(RawScanNoteKind kind, string detail, bool couldHideText, long offset)
    {
        if (index.TryGetValue((kind, detail), out var at))
        {
            var note = notes[at];
            notes[at] = note with { Count = note.Count + 1 };
            return;
        }

        if (notes.Count >= MaxEntries)
        {
            // Beyond the cap everything folds into one entry per kind that could hide text, so nothing is lost silently.
            var key = (kind, "more");
            if (index.TryGetValue(key, out var more))
            {
                notes[more] = notes[more] with { Count = notes[more].Count + 1 };
                return;
            }

            index[key] = notes.Count;
            notes.Add(new RawScanNote(kind, "further entries of this kind", couldHideText, 1, offset));
            return;
        }

        index[(kind, detail)] = notes.Count;
        notes.Add(new RawScanNote(kind, detail, couldHideText, 1, offset));
    }

    public RawScanNotes Build() => new([.. notes], StreamsScanned, InflatedBytes);
}

/// <summary>The last bytes of the file outside stream data: the dictionary of the stream that starts next is in there.</summary>
internal sealed class BodyRing(int capacity)
{
    private readonly byte[] buffer = new byte[capacity];
    private int length;

    public ReadOnlySpan<byte> Span => buffer.AsSpan(0, length);

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length >= buffer.Length)
        {
            data[^buffer.Length..].CopyTo(buffer);
            length = buffer.Length;
            return;
        }

        if (length + data.Length > buffer.Length)
        {
            var keep = buffer.Length - data.Length;
            buffer.AsSpan(length - keep, keep).CopyTo(buffer);
            length = keep;
        }

        data.CopyTo(buffer.AsSpan(length));
        length += data.Length;
    }
}
