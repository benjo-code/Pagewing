using System.Text;

namespace Pagewing.Core.Redaction;

/// <summary>A read-only window on a seekable stream. It sets the position of the inner stream before every read, so several windows and the code that walks the file can share one stream; disposing it leaves the inner stream open.</summary>
internal sealed class RangeStream : Stream
{
    private readonly Stream inner;
    private readonly long start;
    private readonly long end;
    private long position;

    public RangeStream(Stream inner, long start, long end)
    {
        this.inner = inner;
        this.start = start;
        this.end = end;
        position = start;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => end - start;

    public override long Position
    {
        get => position - start;
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, Math.Max(0, end - position));
        if (count <= 0)
        {
            return 0;
        }

        inner.Position = position;
        var read = inner.Read(buffer[..count]);
        position += read;
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Passes the data of a stream on and notes whether the reader ever asked for data after the end. A complete zlib stream is finished
/// before its reader asks for more input; one that is cut off is not, so this is how a truncated stream is told from a whole one
/// (the framework reports a truncated zlib stream as a normal end).
/// </summary>
internal sealed class EndProbeStream(Stream inner) : Stream
{
    /// <summary>Gets whether the reader asked for data after the last byte.</summary>
    public bool ReadPastEnd { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        if (read == 0 && !buffer.IsEmpty)
        {
            ReadPastEnd = true;
        }

        return read;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>The base of the decoders of the simple filters: it reads bytes of the inner stream and asks the decoder for output.</summary>
internal abstract class FilterStream(Stream input) : Stream
{
    private readonly byte[] inputBuffer = new byte[8192];
    private int inputPosition;
    private int inputLength;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(Span<byte> buffer) => buffer.IsEmpty ? 0 : Produce(buffer);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Writes decoded bytes to the buffer; returns how many (0 at the end).</summary>
    protected abstract int Produce(Span<byte> buffer);

    /// <summary>Reads the next byte of the input, or -1 at its end.</summary>
    protected int NextByte()
    {
        if (inputPosition >= inputLength)
        {
            inputLength = input.Read(inputBuffer, 0, inputBuffer.Length);
            inputPosition = 0;
            if (inputLength <= 0)
            {
                inputLength = 0;
                return -1;
            }
        }

        return inputBuffer[inputPosition++];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            input.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>ASCIIHexDecode.</summary>
internal sealed class AsciiHexStream(Stream input) : FilterStream(input)
{
    private int high = -1;
    private bool done;

    protected override int Produce(Span<byte> buffer)
    {
        var count = 0;
        while (count < buffer.Length && !done)
        {
            var c = NextByte();
            if (c < 0 || c == '>')
            {
                done = true;
                if (high >= 0)
                {
                    buffer[count++] = (byte)(high << 4);
                    high = -1;
                }

                break;
            }

            var value = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (value < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = value;
            }
            else
            {
                buffer[count++] = (byte)((high << 4) | value);
                high = -1;
            }
        }

        return count;
    }
}

/// <summary>ASCII85Decode.</summary>
internal sealed class Ascii85Stream(Stream input) : FilterStream(input)
{
    private readonly byte[] pending = new byte[4];
    private int pendingPosition;
    private int pendingLength;
    private uint accumulator;
    private int groupLength;
    private bool done;

    protected override int Produce(Span<byte> buffer)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            if (pendingPosition < pendingLength)
            {
                buffer[count++] = pending[pendingPosition++];
                continue;
            }

            if (done)
            {
                break;
            }

            ReadGroup();
        }

        return count;
    }

    private void ReadGroup()
    {
        pendingPosition = 0;
        pendingLength = 0;
        while (pendingLength == 0 && !done)
        {
            var c = NextByte();
            if (c < 0 || c == '~')
            {
                done = true;
                Finish();
                return;
            }

            if (c <= ' ')
            {
                continue;
            }

            if (c == 'z' && groupLength == 0)
            {
                pending.AsSpan().Clear();
                pendingLength = 4;
                return;
            }

            if (c is < '!' or > 'u')
            {
                continue;
            }

            accumulator = unchecked((accumulator * 85) + (uint)(c - '!'));
            groupLength++;
            if (groupLength == 5)
            {
                Emit(4);
            }
        }
    }

    private void Finish()
    {
        if (groupLength < 2)
        {
            groupLength = 0;
            return;
        }

        var missing = 5 - groupLength;
        for (var i = 0; i < missing; i++)
        {
            accumulator = unchecked((accumulator * 85) + 84);
        }

        Emit(groupLength - 1);
    }

    private void Emit(int bytes)
    {
        pending[0] = (byte)(accumulator >> 24);
        pending[1] = (byte)(accumulator >> 16);
        pending[2] = (byte)(accumulator >> 8);
        pending[3] = (byte)accumulator;
        pendingLength = bytes;
        accumulator = 0;
        groupLength = 0;
    }
}

/// <summary>RunLengthDecode.</summary>
internal sealed class RunLengthStream(Stream input) : FilterStream(input)
{
    private int literalLeft;
    private int repeatLeft;
    private byte repeatByte;
    private bool done;

    protected override int Produce(Span<byte> buffer)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            if (literalLeft > 0)
            {
                var c = NextByte();
                if (c < 0)
                {
                    done = true;
                    literalLeft = 0;
                    break;
                }

                buffer[count++] = (byte)c;
                literalLeft--;
                continue;
            }

            if (repeatLeft > 0)
            {
                buffer[count++] = repeatByte;
                repeatLeft--;
                continue;
            }

            if (done)
            {
                break;
            }

            var length = NextByte();
            if (length is < 0 or 128)
            {
                done = true;
                break;
            }

            if (length < 128)
            {
                literalLeft = length + 1;
                continue;
            }

            var value = NextByte();
            if (value < 0)
            {
                done = true;
                break;
            }

            repeatByte = (byte)value;
            repeatLeft = 257 - length;
        }

        return count;
    }
}

/// <summary>LZWDecode (the variant of PDF: codes of 9 to 12 bits, most significant bit first, clear code 256, end code 257).</summary>
internal sealed class LzwStream(Stream input, int earlyChange) : FilterStream(input)
{
    private const int TableSize = 4096;

    private readonly int[] prefix = new int[TableSize];
    private readonly byte[] suffix = new byte[TableSize];
    private readonly int[] lengths = new int[TableSize];
    private readonly byte[] pending = new byte[TableSize + 1];
    private int pendingPosition;
    private int pendingLength;
    private uint bits;
    private int bitCount;
    private int codeLength = 9;
    private int nextCode = 258;
    private int previous = -1;
    private bool done;

    protected override int Produce(Span<byte> buffer)
    {
        var count = 0;
        while (count < buffer.Length)
        {
            if (pendingPosition < pendingLength)
            {
                var take = Math.Min(buffer.Length - count, pendingLength - pendingPosition);
                pending.AsSpan(pendingPosition, take).CopyTo(buffer[count..]);
                pendingPosition += take;
                count += take;
                continue;
            }

            if (done)
            {
                break;
            }

            DecodeNext();
        }

        return count;
    }

    private void DecodeNext()
    {
        pendingPosition = 0;
        pendingLength = 0;
        while (bitCount < codeLength)
        {
            var c = NextByte();
            if (c < 0)
            {
                done = true;
                return;
            }

            bits = (bits << 8) | (uint)c;
            bitCount += 8;
        }

        var code = (int)((bits >> (bitCount - codeLength)) & ((1u << codeLength) - 1));
        bitCount -= codeLength;
        bits &= (1u << bitCount) - 1;
        if (code == 256)
        {
            codeLength = 9;
            nextCode = 258;
            previous = -1;
            return;
        }

        if (code == 257)
        {
            done = true;
            return;
        }

        if (previous < 0)
        {
            if (code > 255)
            {
                done = true;
                return;
            }

            pending[0] = (byte)code;
            pendingLength = 1;
            previous = code;
            return;
        }

        if (code > nextCode || (code == nextCode && nextCode >= TableSize) || code >= TableSize)
        {
            done = true;
            return;
        }

        int length;
        byte first;
        if (code < nextCode)
        {
            length = Expand(code);
            first = pending[0];
        }
        else
        {
            length = Expand(previous);
            first = pending[0];
            pending[length++] = first;
        }

        if (nextCode < TableSize)
        {
            prefix[nextCode] = previous;
            suffix[nextCode] = first;
            lengths[nextCode] = (previous < 256 ? 1 : lengths[previous]) + 1;
            nextCode++;
        }

        pendingLength = length;
        previous = code;
        if (nextCode + earlyChange >= 2048)
        {
            codeLength = 12;
        }
        else if (nextCode + earlyChange >= 1024)
        {
            codeLength = 11;
        }
        else if (nextCode + earlyChange >= 512)
        {
            codeLength = 10;
        }
    }

    /// <summary>Writes the string of a code to the start of <see cref="pending"/>; returns its length.</summary>
    private int Expand(int code)
    {
        var length = code < 256 ? 1 : lengths[code];
        var at = length;
        var c = code;
        while (at > 0)
        {
            if (c < 256)
            {
                pending[--at] = (byte)c;
                break;
            }

            pending[--at] = suffix[c];
            c = prefix[c];
        }

        return length;
    }
}

/// <summary>What the dictionary of a stream says about its data.</summary>
internal sealed class StreamInfo
{
    /// <summary>Gets the filters in the order they are undone, with their full names.</summary>
    public List<string> Filters { get; } = [];

    /// <summary>Gets or sets whether the stream is an image (pixels, not text).</summary>
    public bool IsImage { get; set; }

    /// <summary>Gets or sets whether the data is known not to be PDF syntax (XMP, a cross-reference stream, a font file).</summary>
    public bool SkipSyntax { get; set; }

    /// <summary>Gets or sets whether a predictor (2 or more) is named.</summary>
    public bool Predictor { get; set; }

    /// <summary>Gets or sets the <c>/EarlyChange</c> value of LZW.</summary>
    public int EarlyChange { get; set; } = 1;

    /// <summary>
    /// Gets or sets why the dictionary could not be read completely, or <see langword="null"/>: the data may then be packed in a way
    /// that is not known, so the stream must be reported (text could hide in it) and never be taken for plain data without a note.
    /// </summary>
    public string? Problem { get; set; }

    /// <summary>Reads the dictionary of the object that ends in the bytes (the last object before the keyword <c>stream</c>).</summary>
    /// <param name="text">The last bytes before the stream data.</param>
    public static StreamInfo Parse(ReadOnlySpan<byte> text)
    {
        var info = new StreamInfo();
        var start = StartOfObject(text, out var ambiguous);
        if (start < 0)
        {
            info.Problem = "the dictionary of the stream was not found (no object header within the bytes read before it)";
            start = 0;
        }
        else if (ambiguous)
        {
            info.Problem = "the dictionary of the stream holds a second object header, so where it starts is not known";
        }

        var tokens = Tokens(text[start..], out var capped);
        if (capped)
        {
            info.Problem ??= "the dictionary of the stream has more entries than are read";
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var (isName, value) = tokens[i];
            if (!isName)
            {
                continue;
            }

            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            switch (value)
            {
                case "Filter":
                    ReadFilters(tokens, i + 1, info);
                    break;
                case "Predictor":
                    if (!next.IsName && int.TryParse(next.Value, out var predictor) && predictor >= 2)
                    {
                        info.Predictor = true;
                    }

                    break;
                case "EarlyChange":
                    if (!next.IsName && int.TryParse(next.Value, out var early))
                    {
                        info.EarlyChange = early;
                    }

                    break;
                case "Subtype":
                    if (next.IsName && next.Value == "Image")
                    {
                        info.IsImage = true;
                    }

                    break;
                case "Type":
                    if (next.IsName && next.Value is "Metadata" or "XRef")
                    {
                        info.SkipSyntax = true;
                    }

                    break;
                case "Length1":
                case "Length2":
                case "Length3":
                    info.SkipSyntax = true;
                    break;
                default:
                    break;
            }
        }

        return info;
    }

    private static string Full(string name) => name switch
    {
        "Fl" => "FlateDecode",
        "AHx" => "ASCIIHexDecode",
        "A85" => "ASCII85Decode",
        "LZW" => "LZWDecode",
        "RL" => "RunLengthDecode",
        "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode",
        _ => name,
    };

    private static void ReadFilters(List<(bool IsName, string Value)> tokens, int at, StreamInfo info)
    {
        const string unreadable = "the filter of the stream is not a name or a list of names (an indirect entry?)";
        if (at >= tokens.Count)
        {
            info.Problem ??= unreadable;
            return;
        }

        if (tokens[at].IsName)
        {
            info.Filters.Add(Full(tokens[at].Value));
            return;
        }

        if (tokens[at].Value == "null")
        {
            return;
        }

        if (tokens[at].Value != "[")
        {
            info.Problem ??= unreadable;
            return;
        }

        var k = at + 1;
        for (; k < tokens.Count && tokens[k].Value != "]"; k++)
        {
            if (tokens[k].IsName)
            {
                info.Filters.Add(Full(tokens[k].Value));
            }
            else
            {
                info.Problem ??= unreadable;
            }
        }

        if (k >= tokens.Count)
        {
            info.Problem ??= unreadable;
        }
    }

    private static int StartOfObject(ReadOnlySpan<byte> text, out bool ambiguous)
    {
        ambiguous = false;
        var end = text.Length;
        while (end > 0)
        {
            var at = text[..end].LastIndexOf("obj"u8);
            if (at < 0)
            {
                return -1;
            }

            if (IsObjectHeader(text, at))
            {
                // A second header since the last finished object: the text between them (a string, a comment) can fake the one that
                // counts, and then entries before it (/Filter) would fall out of the dictionary. Say so instead of guessing.
                var finished = text[..at].LastIndexOf("endobj"u8);
                var from = finished < 0 ? 0 : finished + 6;
                var earlier = from < at ? text[from..at] : default;
                for (var i = earlier.IndexOf("obj"u8); i >= 0 && !earlier.IsEmpty;)
                {
                    if (IsObjectHeader(earlier, i))
                    {
                        ambiguous = true;
                        break;
                    }

                    var next = earlier[(i + 3)..].IndexOf("obj"u8);
                    i = next < 0 ? -1 : i + 3 + next;
                }

                return at + 3;
            }

            end = at;
        }

        return -1;
    }

    private static bool IsObjectHeader(ReadOnlySpan<byte> text, int at)
    {
        var isEndObj = at >= 3 && text.Slice(at - 3, 3).SequenceEqual("end"u8);
        var tokenStart = at == 0 || text[at - 1] <= ' ' || char.IsAsciiDigit((char)text[at - 1]);
        return !isEndObj && tokenStart;
    }

    private static List<(bool IsName, string Value)> Tokens(ReadOnlySpan<byte> text, out bool capped)
    {
        const int maxTokens = 4096;
        capped = false;
        var tokens = new List<(bool, string)>();
        var i = 0;
        while (i < text.Length && tokens.Count < maxTokens)
        {
            var b = text[i];
            if (b <= ' ')
            {
                i++;
            }
            else if (b == '/')
            {
                var j = i + 1;
                while (j < text.Length && text[j] > ' ' && text[j] is not ((byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'/' or (byte)'%'))
                {
                    j++;
                }

                tokens.Add((true, DecodeName(text[(i + 1)..j])));
                i = j;
            }
            else if (b == '(')
            {
                i = SkipString(text, i);
            }
            else if (b == '<')
            {
                if (i + 1 < text.Length && text[i + 1] == '<')
                {
                    i += 2;
                }
                else
                {
                    var close = text[i..].IndexOf((byte)'>');
                    i = close < 0 ? text.Length : i + close + 1;
                }
            }
            else if (b == '%')
            {
                var eol = text[i..].IndexOfAny((byte)'\r', (byte)'\n');
                i = eol < 0 ? text.Length : i + eol;
            }
            else if (b is (byte)'[' or (byte)']')
            {
                tokens.Add((false, ((char)b).ToString()));
                i++;
            }
            else if (b is (byte)'>' or (byte)')' or (byte)'{' or (byte)'}')
            {
                i++;
            }
            else
            {
                var j = i;
                while (j < text.Length && text[j] > ' ' && text[j] is not ((byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'/' or (byte)'%'))
                {
                    j++;
                }

                tokens.Add((false, Encoding.Latin1.GetString(text[i..j])));
                i = j;
            }
        }

        capped = i < text.Length && tokens.Count >= maxTokens;
        return tokens;
    }

    private static int SkipString(ReadOnlySpan<byte> text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            switch (text[i])
            {
                case (byte)'\\':
                    i++;
                    break;
                case (byte)'(':
                    depth++;
                    break;
                case (byte)')':
                    if (--depth == 0)
                    {
                        return i + 1;
                    }

                    break;
                default:
                    break;
            }
        }

        return text.Length;
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '#' && i + 2 < raw.Length && Uri.IsHexDigit((char)raw[i + 1]) && Uri.IsHexDigit((char)raw[i + 2]))
            {
                builder.Append((char)Convert.ToInt32(Encoding.Latin1.GetString(raw.Slice(i + 1, 2)), 16));
                i += 2;
            }
            else
            {
                builder.Append((char)raw[i]);
            }
        }

        return builder.ToString();
    }
}
