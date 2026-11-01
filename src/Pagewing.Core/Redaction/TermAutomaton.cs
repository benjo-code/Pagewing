using System.Buffers;
using System.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// The byte forms of a text as a PDF file can hold it: single bytes (Latin-1, Windows-ANSI, PDFDocEncoding), UTF-8 (XMP) and
/// UTF-16 in both byte orders (a byte order mark sits before the first character and does not change a match). The text is also
/// tried in the composed and the decomposed Unicode form.
/// </summary>
internal static class TermEncodings
{
    private static readonly Dictionary<char, byte> WinAnsiHigh = new()
    {
        ['€'] = 0x80,
        ['‚'] = 0x82,
        ['ƒ'] = 0x83,
        ['„'] = 0x84,
        ['…'] = 0x85,
        ['†'] = 0x86,
        ['‡'] = 0x87,
        ['ˆ'] = 0x88,
        ['‰'] = 0x89,
        ['Š'] = 0x8A,
        ['‹'] = 0x8B,
        ['Œ'] = 0x8C,
        ['Ž'] = 0x8E,
        ['‘'] = 0x91,
        ['’'] = 0x92,
        ['“'] = 0x93,
        ['”'] = 0x94,
        ['•'] = 0x95,
        ['–'] = 0x96,
        ['—'] = 0x97,
        ['˜'] = 0x98,
        ['™'] = 0x99,
        ['š'] = 0x9A,
        ['›'] = 0x9B,
        ['œ'] = 0x9C,
        ['ž'] = 0x9E,
        ['Ÿ'] = 0x9F,
    };

    private static readonly Dictionary<char, byte> PdfDocHigh = new()
    {
        ['˘'] = 0x18,
        ['ˇ'] = 0x19,
        ['ˆ'] = 0x1A,
        ['˙'] = 0x1B,
        ['˝'] = 0x1C,
        ['˛'] = 0x1D,
        ['˚'] = 0x1E,
        ['˜'] = 0x1F,
        ['•'] = 0x80,
        ['†'] = 0x81,
        ['‡'] = 0x82,
        ['…'] = 0x83,
        ['—'] = 0x84,
        ['–'] = 0x85,
        ['ƒ'] = 0x86,
        ['⁄'] = 0x87,
        ['‹'] = 0x88,
        ['›'] = 0x89,
        ['−'] = 0x8A,
        ['‰'] = 0x8B,
        ['„'] = 0x8C,
        ['“'] = 0x8D,
        ['”'] = 0x8E,
        ['‘'] = 0x8F,
        ['’'] = 0x90,
        ['‚'] = 0x91,
        ['™'] = 0x92,
        ['ﬁ'] = 0x93,
        ['ﬂ'] = 0x94,
        ['Ł'] = 0x95,
        ['Œ'] = 0x96,
        ['Š'] = 0x97,
        ['Ÿ'] = 0x98,
        ['Ž'] = 0x99,
        ['ı'] = 0x9A,
        ['ł'] = 0x9B,
        ['œ'] = 0x9C,
        ['š'] = 0x9D,
        ['ž'] = 0x9E,
        ['€'] = 0xA0,
    };

    /// <summary>Gets every distinct byte form of the text (at least one: UTF-8 always exists).</summary>
    public static List<byte[]> Forms(string text)
    {
        var forms = new List<string> { text };
        TryAddNormalized(forms, text, NormalizationForm.FormC);
        TryAddNormalized(forms, text, NormalizationForm.FormD);

        var result = new List<byte[]>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var form in forms)
        {
            Add(result, seen, EncodeSingle(form, Latin1));
            Add(result, seen, EncodeSingle(form, WinAnsi));
            Add(result, seen, EncodeSingle(form, PdfDoc));
            Add(result, seen, Encoding.UTF8.GetBytes(form));
            Add(result, seen, Encoding.BigEndianUnicode.GetBytes(form));
            Add(result, seen, Encoding.Unicode.GetBytes(form));
        }

        return result;
    }

    private static void TryAddNormalized(List<string> forms, string text, NormalizationForm form)
    {
        try
        {
            var normalized = text.Normalize(form);
            if (!forms.Contains(normalized, StringComparer.Ordinal))
            {
                forms.Add(normalized);
            }
        }
        catch (ArgumentException)
        {
            // Not valid Unicode (a lone surrogate): only the form as given is searched.
        }
    }

    private static void Add(List<byte[]> result, HashSet<string> seen, byte[]? bytes)
    {
        if (bytes is { Length: > 0 } && seen.Add(Convert.ToBase64String(bytes)))
        {
            result.Add(bytes);
        }
    }

    private static bool Latin1(char c, out byte value)
    {
        value = (byte)c;
        return c <= 0xFF;
    }

    private static bool WinAnsi(char c, out byte value)
    {
        value = (byte)c;
        return c is (>= ' ' and <= '~') or '\t' or '\n' or '\r' or (>= ' ' and <= 'ÿ') || WinAnsiHigh.TryGetValue(c, out value);
    }

    private static bool PdfDoc(char c, out byte value)
    {
        value = (byte)c;
        return c is (>= ' ' and <= '~') or '\t' or '\n' or '\r' or (>= '¡' and <= 'ÿ' and not '­') || PdfDocHigh.TryGetValue(c, out value);
    }

    private static byte[]? EncodeSingle(string text, TryEncode encode)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            if (!encode(text[i], out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    private delegate bool TryEncode(char c, out byte value);
}

/// <summary>
/// An Aho-Corasick automaton over the byte forms of several texts: one pass over any amount of data finds all of them, with
/// the state carried from chunk to chunk (constant memory). A pattern knows which text it belongs to and its length.
/// </summary>
internal sealed class TermAutomaton
{
    private readonly int[] rootNext;
    private readonly int[] edgeStart;
    private readonly byte[] edgeBytes;
    private readonly int[] edgeTarget;
    private readonly int[] fail;
    private readonly int[] suffix;
    private readonly int[] terminalStart;
    private readonly int[] terminalIds;
    private readonly int[] patternTerm;
    private readonly int[] patternLength;
    private readonly bool[] hasOutput;

    private TermAutomaton(int termCount, List<(int Term, byte[] Bytes)> patterns)
    {
        TermCount = termCount;
        patternTerm = [.. patterns.Select(static p => p.Term)];
        patternLength = [.. patterns.Select(static p => p.Bytes.Length)];
        MaxPatternLength = patternLength.Length == 0 ? 0 : patternLength.Max();

        var children = new List<List<(byte Byte, int Node)>> { new() };
        var terminals = new List<List<int>?> { null };
        for (var p = 0; p < patterns.Count; p++)
        {
            var node = 0;
            foreach (var b in patterns[p].Bytes)
            {
                var list = children[node];
                var next = -1;
                foreach (var (edgeByte, target) in list)
                {
                    if (edgeByte == b)
                    {
                        next = target;
                        break;
                    }
                }

                if (next < 0)
                {
                    next = children.Count;
                    children.Add([]);
                    terminals.Add(null);
                    list.Add((b, next));
                }

                node = next;
            }

            (terminals[node] ??= []).Add(p);
        }

        var nodes = children.Count;
        rootNext = new int[256];
        foreach (var (edgeByte, target) in children[0])
        {
            rootNext[edgeByte] = target;
        }

        edgeStart = new int[nodes + 1];
        var edgeTotal = 0;
        for (var n = 0; n < nodes; n++)
        {
            edgeStart[n] = edgeTotal;
            children[n].Sort(static (a, b) => a.Byte.CompareTo(b.Byte));
            edgeTotal += children[n].Count;
        }

        edgeStart[nodes] = edgeTotal;
        edgeBytes = new byte[edgeTotal];
        edgeTarget = new int[edgeTotal];
        for (var n = 0; n < nodes; n++)
        {
            for (var k = 0; k < children[n].Count; k++)
            {
                edgeBytes[edgeStart[n] + k] = children[n][k].Byte;
                edgeTarget[edgeStart[n] + k] = children[n][k].Node;
            }
        }

        terminalStart = new int[nodes + 1];
        var ids = new List<int>();
        for (var n = 0; n < nodes; n++)
        {
            terminalStart[n] = ids.Count;
            if (terminals[n] is { } here)
            {
                ids.AddRange(here);
            }
        }

        terminalStart[nodes] = ids.Count;
        terminalIds = [.. ids];

        fail = new int[nodes];
        suffix = new int[nodes];
        hasOutput = new bool[nodes];
        var queue = new Queue<int>();
        for (var b = 0; b < 256; b++)
        {
            if (rootNext[b] != 0)
            {
                queue.Enqueue(rootNext[b]);
            }
        }

        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            var link = fail[u];
            suffix[u] = link != 0 && terminalStart[link + 1] > terminalStart[link] ? link : suffix[link];
            hasOutput[u] = terminalStart[u + 1] > terminalStart[u] || suffix[u] != 0;
            for (var e = edgeStart[u]; e < edgeStart[u + 1]; e++)
            {
                var v = edgeTarget[e];
                var f = fail[u];
                int target;
                while (true)
                {
                    target = f == 0 ? rootNext[edgeBytes[e]] : Child(f, edgeBytes[e]);
                    if (target > 0 || f == 0)
                    {
                        break;
                    }

                    f = fail[f];
                }

                fail[v] = target;
                queue.Enqueue(v);
            }
        }

        var first = new List<byte>();
        for (var b = 0; b < 256; b++)
        {
            if (rootNext[b] != 0)
            {
                first.Add((byte)b);
            }
        }

        StartBytes = SearchValues.Create(first.ToArray());
    }

    /// <summary>Gets the number of texts the patterns belong to.</summary>
    public int TermCount { get; }

    /// <summary>Gets the length in bytes of the longest pattern (0 without patterns).</summary>
    public int MaxPatternLength { get; }

    /// <summary>Gets the bytes a pattern can start with; between two matches the scan can jump to the next of them.</summary>
    public SearchValues<byte> StartBytes { get; }

    /// <summary>Builds the automaton for the byte forms of the texts.</summary>
    /// <param name="terms">The texts; the index of a text is the index of its count.</param>
    public static TermAutomaton Build(IReadOnlyList<string> terms)
    {
        var patterns = new List<(int Term, byte[] Bytes)>();
        for (var t = 0; t < terms.Count; t++)
        {
            foreach (var bytes in TermEncodings.Forms(terms[t]))
            {
                patterns.Add((t, bytes));
            }
        }

        return new TermAutomaton(terms.Count, patterns);
    }

    /// <summary>Gets the state after reading a byte in a state.</summary>
    public int Next(int state, byte b)
    {
        while (true)
        {
            if (state == 0)
            {
                return rootNext[b];
            }

            var child = Child(state, b);
            if (child > 0)
            {
                return child;
            }

            state = fail[state];
        }
    }

    /// <summary>Gets whether a pattern ends in the state.</summary>
    public bool HasOutput(int state) => hasOutput[state];

    /// <summary>Gets the first node that ends a pattern in the state (the state itself or the nearest suffix), or 0.</summary>
    public int FirstOutput(int state) => terminalStart[state + 1] > terminalStart[state] ? state : suffix[state];

    /// <summary>Gets the next node that ends a pattern after <paramref name="node"/>, or 0.</summary>
    public int NextOutput(int node) => suffix[node];

    /// <summary>Gets the patterns that end in a node.</summary>
    public ReadOnlySpan<int> PatternsAt(int node) => terminalIds.AsSpan(terminalStart[node], terminalStart[node + 1] - terminalStart[node]);

    /// <summary>Gets the text a pattern belongs to.</summary>
    public int TermOf(int pattern) => patternTerm[pattern];

    /// <summary>Gets the length in bytes of a pattern.</summary>
    public int LengthOf(int pattern) => patternLength[pattern];

    private int Child(int node, byte b)
    {
        var low = edgeStart[node];
        var high = edgeStart[node + 1] - 1;
        while (low <= high)
        {
            var mid = (low + high) >> 1;
            var value = edgeBytes[mid];
            if (value == b)
            {
                return edgeTarget[mid];
            }

            if (value < b)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return 0;
    }
}
