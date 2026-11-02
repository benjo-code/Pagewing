using System.IO.Compression;
using System.Text;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// A tiny PDF writer for the tests of the redaction check: pages with Helvetica text in uncompressed content streams and any extra
/// objects (attachments, metadata, streams with a filter of the test's choice). Strings are written as Latin-1 bytes, so a test
/// writes <c>\337</c> or the character itself for the sharp s. The file has a correct cross-reference table.
/// </summary>
internal sealed class VerifierPdf
{
    private readonly List<byte[]> objects = [];
    private readonly int pageCount;

    public VerifierPdf(params string[] pageContents)
    {
        pageCount = pageContents.Length;
        Reserve(3 + (2 * pageCount)); // catalog, page tree, font, then a page and a content stream per page
        Set(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < pageCount; i++)
        {
            SetStream(5 + (2 * i), string.Empty, Latin1(pageContents[i]));
        }
    }

    /// <summary>Gets or sets text added to the dictionary of the catalog.</summary>
    public string CatalogEntries { get; set; } = string.Empty;

    /// <summary>Gets or sets text added to the trailer.</summary>
    public string TrailerEntries { get; set; } = string.Empty;

    /// <summary>Gets or sets text added to the resource dictionary of every page (for example <c>/XObject &lt;&lt; /Im1 6 0 R &gt;&gt;</c>).</summary>
    public string ResourceEntries { get; set; } = string.Empty;

    /// <summary>Gets or sets text added to the dictionary of every page.</summary>
    public string PageEntries { get; set; } = string.Empty;

    /// <summary>Gets or sets the media box of every page (four numbers).</summary>
    public string MediaBox { get; set; } = "0 0 300 200";

    /// <summary>Gets or sets text added to the dictionary of the page with the same index (after <see cref="PageEntries"/>).</summary>
    public IReadOnlyList<string> PageEntriesByPage { get; set; } = [];

    public static byte[] Latin1(string text) => Encoding.Latin1.GetBytes(text);

    public static byte[] Zlib(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, level, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    /// <summary>The ASCII85 encoding of the data, ending in <c>~&gt;</c>.</summary>
    public static byte[] Ascii85(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < data.Length; i += 4)
        {
            var take = Math.Min(4, data.Length - i);
            uint value = 0;
            for (var k = 0; k < 4; k++)
            {
                value = (value << 8) | (k < take ? data[i + k] : (byte)0);
            }

            if (value == 0 && take == 4)
            {
                builder.Append('z');
                continue;
            }

            var digits = new char[5];
            for (var k = 4; k >= 0; k--)
            {
                digits[k] = (char)('!' + (value % 85));
                value /= 85;
            }

            builder.Append(digits, 0, take + 1);
        }

        builder.Append("~>");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    /// <summary>The RunLength encoding of the data as literal runs only.</summary>
    public static byte[] RunLength(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>();
        for (var i = 0; i < data.Length; i += 128)
        {
            var take = Math.Min(128, data.Length - i);
            output.Add((byte)(take - 1));
            output.AddRange(data.Slice(i, take).ToArray());
        }

        output.Add(128);
        return [.. output];
    }

    /// <summary>Adds an object (its text after <c>n 0 obj</c>); returns its number.</summary>
    public int Add(string body)
    {
        objects.Add(Latin1(body));
        return objects.Count;
    }

    /// <summary>Adds a stream object with a dictionary (without <c>/Length</c>); returns its number.</summary>
    public int AddStream(string dictionary, byte[] data)
    {
        objects.Add(StreamBytes(dictionary, data));
        return objects.Count;
    }

    /// <summary>Builds the file.</summary>
    public byte[] Build()
    {
        var kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(static i => $"{4 + (2 * i)} 0 R"));
        Set(1, $"<< /Type /Catalog /Pages 2 0 R {CatalogEntries} >>");
        Set(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        for (var i = 0; i < pageCount; i++)
        {
            Set(4 + (2 * i), $"<< /Type /Page /Parent 2 0 R /MediaBox [{MediaBox}] /Resources << /Font << /F1 3 0 R >> {ResourceEntries} >> /Contents {5 + (2 * i)} 0 R {PageEntries} {(i < PageEntriesByPage.Count ? PageEntriesByPage[i] : string.Empty)} >>");
        }

        using var file = new MemoryStream();
        Write(file, "%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = file.Position;
            Write(file, $"{i + 1} 0 obj\n");
            file.Write(objects[i]);
            Write(file, "\nendobj\n");
        }

        var xref = file.Position;
        Write(file, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(file, $"{offset:D10} 00000 n \n");
        }

        Write(file, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R {TrailerEntries} >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    private static byte[] StreamBytes(string dictionary, byte[] data)
    {
        using var stream = new MemoryStream();
        Write(stream, $"<< {dictionary} /Length {data.Length} >>\nstream\n");
        stream.Write(data);
        Write(stream, "\nendstream");
        return stream.ToArray();
    }

    private static void Write(Stream stream, string text) => stream.Write(Latin1(text));

    private void Reserve(int count)
    {
        for (var i = 0; i < count; i++)
        {
            objects.Add([]);
        }
    }

    private void Set(int number, string body) => objects[number - 1] = Latin1(body);

    private void SetStream(int number, string dictionary, byte[] data) => objects[number - 1] = StreamBytes(dictionary, data);
}
