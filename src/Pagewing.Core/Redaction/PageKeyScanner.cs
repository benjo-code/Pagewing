using System.Globalization;
using System.Text;

namespace Pagewing.Core.Redaction;

/// <summary>A page that carries keys PDFium cannot remove, found in the bytes of a written file.</summary>
/// <param name="Page">The 0-based page, in the order of the page tree.</param>
/// <param name="Keys">The keys of its page dictionary, in a fixed order: <c>/Thumb</c>, <c>/PieceInfo</c>, <c>/Metadata</c>, <c>/AA</c>.</param>
public sealed record PageKeyFinding(int Page, IReadOnlyList<string> Keys);

/// <summary>
/// The result of <see cref="PageKeyScanner.Scan"/>: either the file was checked and these pages carry the keys (none: the file is
/// clean), or it could not be checked, which is never the same as clean.
/// </summary>
public sealed record PageKeyScan
{
    internal PageKeyScan(IReadOnlyList<PageKeyFinding> findings, string? notChecked, IReadOnlyList<string>? documentKeys = null)
    {
        Findings = findings;
        NotChecked = notChecked;
        DocumentKeys = documentKeys ?? [];
    }

    /// <summary>
    /// Gets the keys of the catalog and the trailer that the cleaning rebuild must have left behind (<see cref="PageKeyScanner.DocumentKeys"/>: the
    /// information dictionary, bookmarks, the names tree with attached files and scripts, page labels, the XMP of the document, open
    /// and document actions, the form and the permissions), in a fixed order; empty when none is there or the file was not checked.
    /// </summary>
    public IReadOnlyList<string> DocumentKeys { get; }

    /// <summary>Gets the pages with such keys, in page order; empty when the file was not checked.</summary>
    public IReadOnlyList<PageKeyFinding> Findings { get; }

    /// <summary>Gets why the file could not be checked (an English line for logs), or <see langword="null"/> when it was checked.</summary>
    public string? NotChecked { get; }

    /// <summary>Gets whether the scan could check the file at all.</summary>
    public bool WasChecked => NotChecked is null;

    /// <summary>Gets whether the file was checked and no page has such a key (the document-level keys are told apart by <see cref="DocumentKeys"/>).</summary>
    public bool IsClean => WasChecked && Findings.Count == 0;

    /// <summary>
    /// Gets the refusal this result stands for, or <see langword="null"/> when the file is clean: <see cref="RedactionRefusalKind.UnremovablePageKeys"/>
    /// with the pages and the keys, or with <see cref="PageKeyScanner.UnverifiedReason"/> when the file could not be checked.
    /// </summary>
    public RedactionRefusal? ToRefusal()
    {
        if (!WasChecked)
        {
            return new RedactionRefusal(RedactionRefusalKind.UnremovablePageKeys, [], PageKeyScanner.UnverifiedReason, PageKeysUnverified: true);
        }

        if (Findings.Count == 0)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var key in PageKeyScanner.Keys)
        {
            var pages = Findings.Where(f => f.Keys.Contains(key)).Select(static f => (f.Page + 1).ToString(CultureInfo.InvariantCulture)).ToList();
            if (pages.Count > 0)
            {
                parts.Add($"{key} on {(pages.Count == 1 ? "page" : "pages")} {string.Join(", ", pages)}");
            }
        }

        return new RedactionRefusal(
            RedactionRefusalKind.UnremovablePageKeys,
            [.. Findings.Select(static f => f.Page)],
            $"Page keys that cannot be removed: {string.Join("; ", parts)}.");
    }
}

/// <summary>
/// Finds the page keys that PDFium cannot remove (<c>/Thumb</c>, <c>/PieceInfo</c>, <c>/Metadata</c>, <c>/AA</c> in a page dictionary)
/// in the bytes PDFium wrote. It tokenizes the file (strings, comments and stream data are skipped as the syntax says, names are
/// decoded) and looks only at the top-level keys of the dictionaries of the objects in the page tree: a name like
/// <c>/AAAAAA+Geist</c> or a <c>/Thumb</c> in an annotation or a resource dictionary is no finding. It never reads the original file
/// (which may keep objects in object streams) and it fails closed: whatever it cannot read for sure (an object stream, which hides
/// objects from a tokenizer; a page tree it cannot walk; a different number of pages than PDFium reports; an object defined twice;
/// a stream whose length it cannot trust) ends in a result that was not checked, never in "nothing found". The result of a scan
/// is only as good as the assumption that PDFium writes page dictionaries as plain objects (it keeps a cross-reference stream if
/// the source had one, but never writes object streams); the test <c>PdfiumWritesPageDictionariesUncompressed</c> guards it.
/// </summary>
public static class PageKeyScanner
{
    /// <summary>
    /// The reason a refusal carries when the keys could not be checked (fixed English text). The interface does not compare it: it
    /// reads <see cref="RedactionRefusal.PageKeysUnverified"/> and translates.
    /// </summary>
    public const string UnverifiedReason = "The page keys could not be checked.";

    /// <summary>Gets the keys that are looked for, in the order a finding lists them.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["/Thumb", "/PieceInfo", "/Metadata", "/AA"];

    /// <summary>
    /// Gets the keys of the catalog (and, for <c>/Info</c>, the trailer) that hold what the cleaning rebuild removes; a scan of the rebuilt
    /// file reports every one that is there (<see cref="PageKeyScan.DocumentKeys"/>), in this order.
    /// </summary>
    public static IReadOnlyList<string> DocumentKeys { get; } = ["/Info", "/Metadata", "/Outlines", "/Names", "/PageLabels", "/OpenAction", "/AA", "/AcroForm", "/Perms"];

    /// <summary>Scans the bytes of a file that PDFium wrote; a cross-reference stream is read as the trailer.</summary>
    /// <param name="written">The file; it must be readable and seekable and is read from its start, never changed.</param>
    /// <param name="pdfiumPageCount">The number of pages PDFium reports for this file; the scan must find exactly as many pages.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The pages with such keys, or the statement that the file could not be checked.</returns>
    /// <exception cref="ArgumentException">The stream is not readable and seekable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static PageKeyScan Scan(Stream written, int pdfiumPageCount, CancellationToken cancellationToken = default) =>
        Scan(written, pdfiumPageCount, allowCrossReferenceStream: true, cancellationToken);

    /// <summary>Scans the bytes of a file that PDFium wrote.</summary>
    /// <param name="written">The file; it must be readable and seekable and is read from its start, never changed.</param>
    /// <param name="pdfiumPageCount">The number of pages PDFium reports for this file; the scan must find exactly as many pages.</param>
    /// <param name="allowCrossReferenceStream">
    /// Whether a cross-reference stream (<c>/Type /XRef</c>) is read as the trailer (PDFium writes one for a source that had one). With
    /// <see langword="false"/> it makes the file "could not be checked", like an object stream: the check of the final output uses this,
    /// because the rebuilt file has a classic table and anything else there is unexpected.
    /// </param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    /// <returns>The pages with such keys, or the statement that the file could not be checked.</returns>
    /// <exception cref="ArgumentException">The stream is not readable and seekable.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static PageKeyScan Scan(Stream written, int pdfiumPageCount, bool allowCrossReferenceStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(written);
        if (!written.CanRead || !written.CanSeek)
        {
            throw new ArgumentException("The file must be a readable, seekable stream.", nameof(written));
        }

        try
        {
            written.Position = 0;
            return new PageKeyReader(written, allowCrossReferenceStream, cancellationToken).Run(pdfiumPageCount);
        }
        catch (Unverifiable ex)
        {
            return new PageKeyScan([], ex.Message);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ObjectDisposedException)
        {
            // A file that cannot be read cannot be checked; an unexpected failure must not read as "clean" either.
            return new PageKeyScan([], $"The file could not be read ({ex.GetType().Name}).");
        }
    }

    /// <summary>Scans the file and returns the refusal that follows (see <see cref="PageKeyScan.ToRefusal"/>), or <see langword="null"/> when it is clean.</summary>
    /// <param name="written">The file PDFium wrote; readable and seekable.</param>
    /// <param name="pdfiumPageCount">The number of pages PDFium reports for it.</param>
    /// <param name="cancellationToken">Cancels the scan.</param>
    public static RedactionRefusal? Refusal(Stream written, int pdfiumPageCount, CancellationToken cancellationToken = default) =>
        Scan(written, pdfiumPageCount, cancellationToken).ToRefusal();

    private sealed class Unverifiable(string message) : Exception(message)
    {
    }

    private enum TokenKind : byte
    {
        End,
        Int,
        Real,
        Name,
        String,
        ArrayStart,
        ArrayEnd,
        DictStart,
        DictEnd,
        Keyword,
        Stray,
    }

    private enum ValueKind : byte
    {
        Other,
        Int,
        Name,
        Ref,
        Array,
    }

    private enum ObjectType : byte
    {
        None,
        NotADictionary,
        Page,
        Pages,
        Catalog,
        Other,
    }

    private readonly record struct Token(TokenKind Kind, long Int, string? Text);

    /// <summary>A parsed value; only what the scan needs is kept (names, integers, references, and arrays of them).</summary>
    private sealed class Value(ValueKind kind, long number = 0, string? text = null, List<Value>? items = null)
    {
        public static readonly Value Other = new(ValueKind.Other);

        public ValueKind Kind { get; } = kind;

        public long Number { get; } = number;

        public string? Text { get; } = text;

        public List<Value>? Items { get; } = items;
    }

    /// <summary>What the scan keeps of the top-level dictionary of an object.</summary>
    private sealed class DictInfo
    {
        public ushort Mask { get; set; }

        public Value? Type { get; set; }

        public Value? Kids { get; set; }

        public Value? Pages { get; set; }

        public Value? Length { get; set; }

        public Value? Root { get; set; }
    }

    private readonly record struct ObjectInfo(ObjectType Type, ushort Mask, int PagesReference, int[]? Kids);

    private sealed class PageKeyReader
    {
        private const int BufferSize = 64 * 1024;
        private const int MaxDepth = 128;
        private const int MaxTokenText = 256;
        private const int MaxKids = 10_000_000;

        private readonly Stream stream;
        private readonly bool allowCrossReferenceStream;
        private readonly CancellationToken cancellation;
        private readonly byte[] buffer = new byte[BufferSize];
        private readonly Token[] lookahead = new Token[2];
        private readonly Dictionary<int, ObjectInfo> objects = [];
        private readonly StringBuilder text = new();
        private int lookaheadCount;
        private int position;
        private int length;
        private long bufferStart;
        private int rootReference = -1;
        private ushort trailerMask;
        private int pageTypedObjects;

        public PageKeyReader(Stream stream, bool allowCrossReferenceStream, CancellationToken cancellation)
        {
            this.stream = stream;
            this.allowCrossReferenceStream = allowCrossReferenceStream;
            this.cancellation = cancellation;
        }

        public PageKeyScan Run(int pdfiumPageCount)
        {
            ReadTopLevel();
            return BuildResult(pdfiumPageCount);
        }

        private static bool IsWhite(int b) => b is 0 or 9 or 10 or 12 or 13 or 32;

        private static bool IsDelimiter(int b) => b is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

        private static Unverifiable Fail(string reason) => new(reason);

        private const ushort PageKeyBits = 15;

        private static ushort MaskOf(string name) => name switch
        {
            "Thumb" => 1,
            "PieceInfo" => 2,
            "Metadata" => 4,
            "AA" => 8,
            "Outlines" => 16,
            "Names" => 32,
            "PageLabels" => 64,
            "OpenAction" => 128,
            "AcroForm" => 256,
            "Perms" => 512,
            "Info" => 1024,
            _ => 0,
        };

        private static readonly ushort[] DocumentKeyBits = [1024, 4, 16, 32, 64, 128, 8, 256, 512];

        private static string[] KeysOf(ushort mask)
        {
            var keys = new List<string>(4);
            for (var i = 0; i < Keys.Count; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    keys.Add(Keys[i]);
                }
            }

            return [.. keys];
        }

        // ------------------------------------------------------------------ the file, top level

        private void ReadTopLevel()
        {
            long first = -1;
            long second = -1;
            for (; ; )
            {
                var token = Next();
                switch (token.Kind)
                {
                    case TokenKind.End:
                        return;

                    case TokenKind.Int:
                        first = second;
                        second = token.Int;
                        continue;

                    case TokenKind.Keyword when token.Text == "obj":
                        if (first < 0 || second < 0 || first > int.MaxValue)
                        {
                            throw Fail("An object keyword without an object number.");
                        }

                        ReadObject((int)first);
                        break;

                    case TokenKind.Keyword when token.Text == "trailer":
                        ReadTrailer();
                        break;

                    case TokenKind.ArrayStart:
                    case TokenKind.DictStart:
                        _ = ParseValue(token, 0, keep: false);
                        break;

                    case TokenKind.ArrayEnd:
                    case TokenKind.DictEnd:
                    case TokenKind.Stray:
                        throw Fail("Unexpected syntax outside of an object.");
                }

                first = -1;
                second = -1;
            }
        }

        private void ReadTrailer()
        {
            var start = Next();
            if (start.Kind != TokenKind.DictStart)
            {
                throw Fail("A trailer without a dictionary.");
            }

            TakeRoot(ParseDictionary(0, top: true));
        }

        /// <summary>Takes the catalog from a trailer, or from the dictionary of a cross-reference stream, which is the trailer then.</summary>
        private void TakeRoot(DictInfo dictionary)
        {
            trailerMask |= dictionary.Mask;
            if (dictionary.Root is { Kind: ValueKind.Ref } root)
            {
                var reference = (int)root.Number;
                if (rootReference >= 0 && rootReference != reference)
                {
                    throw Fail("Two trailers name different catalogs.");
                }

                rootReference = reference;
            }
        }

        private void ReadObject(int number)
        {
            if (objects.ContainsKey(number))
            {
                throw Fail("An object is defined twice.");
            }

            var first = Next();
            if (first.Kind != TokenKind.DictStart)
            {
                _ = ParseValue(first, 0, keep: false);
                if (Peek(0) is { Kind: TokenKind.Keyword, Text: "stream" })
                {
                    throw Fail("A stream whose dictionary is not a dictionary.");
                }

                objects[number] = new ObjectInfo(ObjectType.NotADictionary, 0, -1, null);
                return;
            }

            var dictionary = ParseDictionary(0, top: true);
            var type = dictionary.Type is { Kind: ValueKind.Name } name ? name.Text : null;
            if (type == "ObjStm")
            {
                // Objects inside it (page dictionaries among them) cannot be seen without unpacking it.
                throw Fail("The file has an object stream.");
            }

            if (Peek(0) is { Kind: TokenKind.Keyword, Text: "stream" })
            {
                if (type == "XRef" && !allowCrossReferenceStream)
                {
                    // The rebuilt file has a classic table; a cross-reference stream there is not what the scan was made for.
                    throw Fail("The file has a cross-reference stream.");
                }

                SkipStream(dictionary);
                if (type == "XRef")
                {
                    // PDFium writes a cross-reference stream when the file it rewrites had one, with all objects as plain objects;
                    // its dictionary is the trailer. Were objects hidden in object streams, the ObjStm above would have failed the scan.
                    TakeRoot(dictionary);
                }
            }

            objects[number] = Describe(dictionary, type);
        }

        private ObjectInfo Describe(DictInfo dictionary, string? type)
        {
            var objectType = type switch
            {
                "Page" => ObjectType.Page,
                "Pages" => ObjectType.Pages,
                "Catalog" => ObjectType.Catalog,
                _ => ObjectType.Other,
            };
            if (objectType == ObjectType.Page)
            {
                pageTypedObjects++;
            }
            else if (dictionary.Type is { Kind: not ValueKind.Name })
            {
                // A /Type that is not a name (an indirect reference): this could be a page dictionary that the scan cannot recognize.
                objectType = ObjectType.None;
            }

            int[]? kids = null;
            if (dictionary.Kids is { } kidsValue)
            {
                if (kidsValue.Kind != ValueKind.Array || kidsValue.Items is null)
                {
                    throw Fail("A /Kids entry is not an array.");
                }

                kids = new int[kidsValue.Items.Count];
                for (var i = 0; i < kids.Length; i++)
                {
                    if (kidsValue.Items[i].Kind != ValueKind.Ref)
                    {
                        throw Fail("A /Kids entry holds something other than references.");
                    }

                    kids[i] = (int)kidsValue.Items[i].Number;
                }
            }

            var pagesReference = dictionary.Pages is { Kind: ValueKind.Ref } pages ? (int)pages.Number : -1;
            return new ObjectInfo(objectType, dictionary.Mask, pagesReference, kids);
        }

        private void SkipStream(DictInfo dictionary)
        {
            _ = Next(); // the keyword "stream"
            if (lookaheadCount != 0)
            {
                throw Fail("Stream data starts where the scan already looked ahead.");
            }

            if (dictionary.Length is not { Kind: ValueKind.Int } declared || declared.Number < 0)
            {
                throw Fail("A stream whose length is not a plain number.");
            }

            var b = ReadByte();
            if (b == '\r')
            {
                if (PeekByte() == '\n')
                {
                    ReadByte();
                }
            }
            else if (b != '\n')
            {
                throw Fail("The keyword stream is not followed by a line end.");
            }

            Skip(declared.Number);
            if (Next() is not { Kind: TokenKind.Keyword, Text: "endstream" })
            {
                throw Fail("Stream data does not end where its length says.");
            }
        }

        // ------------------------------------------------------------------ result

        private PageKeyScan BuildResult(int pdfiumPageCount)
        {
            if (rootReference < 0)
            {
                throw Fail("The trailer names no catalog.");
            }

            if (!objects.TryGetValue(rootReference, out var catalog) || catalog.PagesReference < 0)
            {
                throw Fail("The catalog is missing or names no page tree.");
            }

            var leaves = new List<int>();
            var visited = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(catalog.PagesReference);
            while (stack.Count > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                var number = stack.Pop();
                if (!visited.Add(number))
                {
                    throw Fail("The page tree visits an object twice.");
                }

                if (!objects.TryGetValue(number, out var node))
                {
                    throw Fail("The page tree names an object that is not in the file.");
                }

                if (node.Kids is { } kids)
                {
                    if (kids.Length > MaxKids)
                    {
                        throw Fail("A page tree node has too many children.");
                    }

                    for (var i = kids.Length - 1; i >= 0; i--)
                    {
                        stack.Push(kids[i]);
                    }
                }
                else if (node.Type == ObjectType.Page)
                {
                    leaves.Add(number);
                }
                else if (node.Type != ObjectType.Pages)
                {
                    throw Fail("The page tree holds an object that is neither a page nor a page tree node.");
                }
            }

            if (leaves.Count != pdfiumPageCount)
            {
                throw Fail($"The page tree has {leaves.Count} pages, PDFium reports {pdfiumPageCount}.");
            }

            if (pageTypedObjects != leaves.Count)
            {
                throw Fail("The file has page dictionaries that are not in the page tree.");
            }

            var findings = new List<PageKeyFinding>();
            for (var page = 0; page < leaves.Count; page++)
            {
                var mask = (ushort)(objects[leaves[page]].Mask & PageKeyBits);
                if (mask != 0)
                {
                    findings.Add(new PageKeyFinding(page, KeysOf(mask)));
                }
            }

            // What the rebuild must have left behind: the keys of the catalog and the information dictionary of the trailer.
            var documentMask = (ushort)(catalog.Mask | (trailerMask & 1024));
            var documentKeys = new List<string>();
            for (var i = 0; i < DocumentKeyBits.Length; i++)
            {
                if ((documentMask & DocumentKeyBits[i]) != 0)
                {
                    documentKeys.Add(DocumentKeys[i]);
                }
            }

            return new PageKeyScan(findings, null, documentKeys);
        }

        // ------------------------------------------------------------------ values

        private DictInfo ParseDictionary(int depth, bool top)
        {
            if (depth > MaxDepth)
            {
                throw Fail("The syntax is nested too deeply.");
            }

            var info = new DictInfo();
            for (; ; )
            {
                var key = Next();
                if (key.Kind == TokenKind.DictEnd)
                {
                    return info;
                }

                if (key.Kind != TokenKind.Name || key.Text is null)
                {
                    throw Fail("A dictionary key is not a name.");
                }

                var name = key.Text;
                var keep = top && name is "Type" or "Kids" or "Pages" or "Length" or "Root";
                if (top)
                {
                    info.Mask |= MaskOf(name);
                }

                var value = ParseValue(Next(), depth + 1, keep);
                if (keep)
                {
                    switch (name)
                    {
                        case "Type":
                            info.Type = value;
                            break;
                        case "Kids":
                            info.Kids = value;
                            break;
                        case "Pages":
                            info.Pages = value;
                            break;
                        case "Length":
                            info.Length = value;
                            break;
                        case "Root":
                            info.Root = value;
                            break;
                    }
                }
            }
        }

        private Value ParseValue(Token token, int depth, bool keep)
        {
            if (depth > MaxDepth)
            {
                throw Fail("The syntax is nested too deeply.");
            }

            switch (token.Kind)
            {
                case TokenKind.Int:
                    if (Peek(0).Kind == TokenKind.Int && Peek(0).Int >= 0 && Peek(1) is { Kind: TokenKind.Keyword, Text: "R" })
                    {
                        _ = Next();
                        _ = Next();
                        return keep ? new Value(ValueKind.Ref, token.Int) : Value.Other;
                    }

                    return keep ? new Value(ValueKind.Int, token.Int) : Value.Other;

                case TokenKind.Name:
                    return keep ? new Value(ValueKind.Name, text: token.Text) : Value.Other;

                case TokenKind.Real:
                case TokenKind.String:
                    return Value.Other;

                case TokenKind.Keyword when token.Text is "true" or "false" or "null":
                    return Value.Other;

                case TokenKind.ArrayStart:
                    {
                        var items = keep ? new List<Value>() : null;
                        for (; ; )
                        {
                            var next = Next();
                            if (next.Kind == TokenKind.ArrayEnd)
                            {
                                return keep ? new Value(ValueKind.Array, items: items) : Value.Other;
                            }

                            var item = ParseValue(next, depth + 1, keep);
                            if (items is not null)
                            {
                                if (items.Count >= MaxKids)
                                {
                                    throw Fail("An array is too long.");
                                }

                                items.Add(item);
                            }
                        }
                    }

                case TokenKind.DictStart:
                    _ = ParseDictionary(depth + 1, top: false);
                    return Value.Other;

                default:
                    throw Fail("Unexpected syntax in a value.");
            }
        }

        // ------------------------------------------------------------------ tokens

        private Token Peek(int index)
        {
            while (lookaheadCount <= index)
            {
                lookahead[lookaheadCount++] = Lex();
            }

            return lookahead[index];
        }

        private Token Next()
        {
            if (lookaheadCount > 0)
            {
                var token = lookahead[0];
                lookahead[0] = lookahead[1];
                lookaheadCount--;
                return token;
            }

            return Lex();
        }

        private Token Lex()
        {
            for (; ; )
            {
                var b = ReadByte();
                if (b < 0)
                {
                    return new Token(TokenKind.End, 0, null);
                }

                if (IsWhite(b))
                {
                    continue;
                }

                switch (b)
                {
                    case '%':
                        while ((b = ReadByte()) >= 0 && b != '\n' && b != '\r')
                        {
                        }

                        continue;
                    case '/':
                        return new Token(TokenKind.Name, 0, ReadName());
                    case '(':
                        SkipLiteralString();
                        return new Token(TokenKind.String, 0, null);
                    case '<':
                        if (PeekByte() == '<')
                        {
                            ReadByte();
                            return new Token(TokenKind.DictStart, 0, null);
                        }

                        SkipHexString();
                        return new Token(TokenKind.String, 0, null);
                    case '>':
                        if (PeekByte() == '>')
                        {
                            ReadByte();
                            return new Token(TokenKind.DictEnd, 0, null);
                        }

                        return new Token(TokenKind.Stray, 0, null);
                    case '[':
                        return new Token(TokenKind.ArrayStart, 0, null);
                    case ']':
                        return new Token(TokenKind.ArrayEnd, 0, null);
                    case ')':
                    case '{':
                    case '}':
                        return new Token(TokenKind.Stray, 0, null);
                    default:
                        return ReadRegular(b);
                }
            }
        }

        private Token ReadRegular(int first)
        {
            text.Clear();
            text.Append((char)first);
            int b;
            while ((b = PeekByte()) >= 0 && !IsWhite(b) && !IsDelimiter(b))
            {
                ReadByte();
                if (text.Length < MaxTokenText)
                {
                    text.Append((char)b);
                }
            }

            // A number is an optional sign and digits with at most one point; anything else is a keyword (obj, R, stream, true ...).
            var start = text[0] is '+' or '-' ? 1 : 0;
            var digitCount = 0;
            var points = 0;
            var numeric = text.Length > start;
            for (var i = start; i < text.Length && numeric; i++)
            {
                var c = text[i];
                if (c is >= '0' and <= '9')
                {
                    digitCount++;
                }
                else if (c == '.')
                {
                    points++;
                }
                else
                {
                    numeric = false;
                }
            }

            if (numeric && digitCount > 0 && points == 0 && digitCount <= 18)
            {
                var value = 0L;
                for (var i = start; i < text.Length; i++)
                {
                    value = (value * 10) + (text[i] - '0');
                }

                return new Token(TokenKind.Int, text[0] == '-' ? -value : value, null);
            }

            if (numeric && digitCount > 0 && points <= 1)
            {
                return new Token(TokenKind.Real, 0, null);
            }

            return new Token(TokenKind.Keyword, 0, text.ToString());
        }

        private string ReadName()
        {
            text.Clear();
            int b;
            while ((b = PeekByte()) >= 0 && !IsWhite(b) && !IsDelimiter(b))
            {
                ReadByte();
                if (b == '#')
                {
                    // "#xx" is the character with that code; a '#' without two hexadecimal digits after it is just a '#'.
                    var high = PeekByte();
                    if (IsHex(high))
                    {
                        ReadByte();
                        var low = PeekByte();
                        if (IsHex(low))
                        {
                            ReadByte();
                            b = (HexValue(high) << 4) | HexValue(low);
                        }
                        else
                        {
                            Append('#');
                            b = high;
                        }
                    }
                }

                Append((char)b);
            }

            return text.ToString();
        }

        private void Append(char c)
        {
            if (text.Length < MaxTokenText)
            {
                text.Append(c);
            }
        }

        private static bool IsHex(int b) => b is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

        private static int HexValue(int b) => b <= '9' ? b - '0' : (b | 0x20) - 'a' + 10;

        private void SkipLiteralString()
        {
            var depth = 1;
            int b;
            while ((b = ReadByte()) >= 0)
            {
                switch (b)
                {
                    case '\\':
                        if (ReadByte() < 0)
                        {
                            throw Fail("A string is not closed.");
                        }

                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        if (--depth == 0)
                        {
                            return;
                        }

                        break;
                }
            }

            throw Fail("A string is not closed.");
        }

        private void SkipHexString()
        {
            int b;
            while ((b = ReadByte()) >= 0)
            {
                if (b == '>')
                {
                    return;
                }

                if (!IsHex(b) && !IsWhite(b))
                {
                    throw Fail("A hexadecimal string holds another character.");
                }
            }

            throw Fail("A string is not closed.");
        }

        // ------------------------------------------------------------------ bytes

        private bool Fill()
        {
            cancellation.ThrowIfCancellationRequested();
            bufferStart += length;
            position = 0;
            length = stream.Read(buffer, 0, buffer.Length);
            return length > 0;
        }

        private int PeekByte()
        {
            if (position == length && !Fill())
            {
                return -1;
            }

            return buffer[position];
        }

        private int ReadByte()
        {
            var b = PeekByte();
            if (b >= 0)
            {
                position++;
            }

            return b;
        }

        private void Skip(long count)
        {
            var available = length - position;
            if (count <= available)
            {
                position += (int)count;
                return;
            }

            var target = bufferStart + position + count;
            if (target > stream.Length)
            {
                throw Fail("A stream reaches past the end of the file.");
            }

            stream.Position = target;
            bufferStart = target;
            position = 0;
            length = 0;
        }
    }
}
