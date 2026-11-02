namespace Pagewing.Core.Redaction;

/// <summary>
/// Reads PDF syntax a chunk at a time (the state is kept between chunks) and searches the texts in what a reader of the file would
/// see: literal strings with their escapes (<c>\ddd</c>, <c>\n</c>, <c>\(</c>, a line continuation), hex strings, names with
/// <c>#xx</c> escapes, and the strings of an array (a <c>TJ</c> with kerning between the pieces) or of a text object
/// (<c>BT</c> ... <c>ET</c>) put together. It also tells where stream data starts (the keyword <c>stream</c> in the file's own
/// syntax, not in a string or a comment), and counts the names a cleanup is interested in.
/// </summary>
internal sealed class RawSyntaxScanner
{
    private const int TokenCapacity = 16;
    private const int NameCapacity = 256;
    private const int DictFlagDepth = 64;
    private const int MaxArrayDepth = 1000;
    private const byte Lf = 10;
    private const byte Cr = 13;
    private const byte Space = 32;
    private const byte Tab = 9;

    private readonly NameTally tally;
    private readonly bool detectStreams;
    private readonly StringChannel? stringChannel;
    private readonly StringChannel? arrayChannel;
    private readonly StringChannel? textChannel;
    private readonly StringChannel? nameChannel;
    private readonly byte[] token = new byte[TokenCapacity];
    private readonly byte[] name = new byte[NameCapacity];
    private readonly long[] nameOffsets = new long[NameCapacity];
    private readonly byte[] decodedName = new byte[NameCapacity];
    private readonly long[] decodedNameOffsets = new long[NameCapacity];
    private readonly byte[] dictFlags = new byte[DictFlagDepth];

    private Mode mode;
    private int tokenLength;
    private int nameLength;
    private int parenDepth;
    private int arrayDepth;
    private int dictDepth;
    private bool textObject;
    private bool previousGreater;
    private bool previousNameWasType;
    private Escape escape;
    private int octalValue;
    private int octalDigits;
    private long escapeOffset;
    private int hexHigh = -1;
    private long hexOffset;
    private bool streamStart;

    public RawSyntaxScanner(TermAutomaton? automaton, IntervalStore? store, NameTally tally, bool detectStreams)
    {
        this.tally = tally;
        this.detectStreams = detectStreams;
        if (automaton is { MaxPatternLength: > 0 } && store is not null)
        {
            stringChannel = new StringChannel(automaton, store);
            arrayChannel = new StringChannel(automaton, store);
            textChannel = new StringChannel(automaton, store);
            nameChannel = new StringChannel(automaton, store);
        }
    }

    private enum Mode : byte
    {
        Normal,
        Comment,
        Literal,
        Hex,
        Name,
        Angle,
        StreamSpace,
        StreamCarriageReturn,
    }

    private enum Escape : byte
    {
        None,
        Backslash,
        Octal,
        AfterCarriageReturn,
        AfterContinuationCarriageReturn,
    }

    /// <summary>Gets whether the last <see cref="Feed"/> stopped because stream data starts right after the bytes it consumed.</summary>
    public bool StreamStart => streamStart;

    /// <summary>Forgets everything but the counted names: the data that follows is not contiguous with what was fed before.</summary>
    public void Reset()
    {
        mode = Mode.Normal;
        tokenLength = 0;
        nameLength = 0;
        parenDepth = 0;
        arrayDepth = 0;
        dictDepth = 0;
        textObject = false;
        previousGreater = false;
        previousNameWasType = false;
        escape = Escape.None;
        hexHigh = -1;
        streamStart = false;
    }

    /// <summary>Reads a chunk; stops early, right after the line end that follows the keyword <c>stream</c>, if stream data starts.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="offset">The offset of the first byte in the coordinate space of the matches.</param>
    /// <returns>The number of bytes consumed: all of them unless <see cref="StreamStart"/> is set.</returns>
    public int Feed(ReadOnlySpan<byte> data, long offset)
    {
        streamStart = false;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            var at = offset + i;
            switch (mode)
            {
                case Mode.Normal:
                    if (IsRegular(b))
                    {
                        if (tokenLength < TokenCapacity)
                        {
                            token[tokenLength] = b;
                        }

                        tokenLength++;
                        previousGreater = false;
                        break;
                    }

                    if (EndToken(b))
                    {
                        if (b == '\n')
                        {
                            streamStart = true;
                            return i + 1;
                        }

                        mode = b == '\r' ? Mode.StreamCarriageReturn : Mode.StreamSpace;
                        break;
                    }

                    Delimiter(b, at);
                    break;

                case Mode.StreamSpace:
                    if (b is Space or Tab)
                    {
                        break;
                    }

                    if (b == '\n')
                    {
                        mode = Mode.Normal;
                        streamStart = true;
                        return i + 1;
                    }

                    mode = b == '\r' ? Mode.StreamCarriageReturn : Mode.Normal;
                    if (mode == Mode.Normal)
                    {
                        i--; // not the line end after all: the byte is read as a normal byte
                    }

                    break;

                case Mode.StreamCarriageReturn:
                    mode = Mode.Normal;
                    streamStart = true;
                    return b == '\n' ? i + 1 : i;

                case Mode.Comment:
                    if (b is Lf or Cr)
                    {
                        mode = Mode.Normal;
                    }

                    break;

                case Mode.Literal:
                    ReadLiteral(b, at);
                    break;

                case Mode.Hex:
                    ReadHex(b, at);
                    break;

                case Mode.Angle:
                    if (b == (byte)'<')
                    {
                        OpenDictionary();
                        mode = Mode.Normal;
                    }
                    else
                    {
                        StartHex();
                        i--;
                    }

                    break;

                case Mode.Name:
                    if (IsRegular(b))
                    {
                        if (nameLength < NameCapacity)
                        {
                            name[nameLength] = b;
                            nameOffsets[nameLength] = at;
                        }

                        nameLength++;
                    }
                    else
                    {
                        EndName();
                        mode = Mode.Normal;
                        i--;
                    }

                    break;

                default:
                    break;
            }
        }

        return data.Length;
    }

    private static bool IsRegular(byte b) =>
        b > Space && b is not ((byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%');

    private static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    /// <summary>Handles the token that a white space byte or a delimiter ends; returns whether it was the keyword <c>stream</c> followed by a line end part.</summary>
    private bool EndToken(byte terminator)
    {
        var length = tokenLength;
        tokenLength = 0;
        if (length == 0)
        {
            return false;
        }

        previousNameWasType = false;
        if (length == 2 && token[0] == (byte)'B' && token[1] == (byte)'T')
        {
            textObject = true;
            textChannel?.Reset();
        }
        else if (length == 2 && token[0] == (byte)'E' && token[1] == (byte)'T')
        {
            textObject = false;
        }
        else if (detectStreams && length == 6 && token.AsSpan(0, 6).SequenceEqual("stream"u8) && terminator is Lf or Cr or Space or Tab)
        {
            return true;
        }

        return false;
    }

    private void Delimiter(byte b, long at)
    {
        if (b != '>')
        {
            previousGreater = false;
        }

        switch (b)
        {
            case (byte)'(':
                previousNameWasType = false;
                mode = Mode.Literal;
                parenDepth = 1;
                escape = Escape.None;
                stringChannel?.Reset();
                break;
            case (byte)'<':
                mode = Mode.Angle;
                break;
            case (byte)'>':
                if (previousGreater)
                {
                    CloseDictionary();
                    previousGreater = false;
                }
                else
                {
                    previousGreater = true;
                }

                break;
            case (byte)'[':
                if (arrayDepth < MaxArrayDepth && ++arrayDepth == 1)
                {
                    arrayChannel?.Reset();
                }

                break;
            case (byte)']':
                if (arrayDepth > 0)
                {
                    arrayDepth--;
                }

                break;
            case (byte)'/':
                mode = Mode.Name;
                nameLength = 0;
                break;
            case (byte)'%':
                mode = Mode.Comment;
                break;
            default:
                break;
        }
    }

    private void OpenDictionary()
    {
        if (dictDepth < DictFlagDepth)
        {
            dictFlags[dictDepth] = 0;
        }

        dictDepth++;
    }

    private void CloseDictionary()
    {
        if (dictDepth == 0)
        {
            return;
        }

        dictDepth--;
        if (dictDepth < DictFlagDepth && dictFlags[dictDepth] == 3)
        {
            tally.OnOutlineItem();
        }
    }

    private void StartHex()
    {
        previousNameWasType = false;
        mode = Mode.Hex;
        hexHigh = -1;
        stringChannel?.Reset();
    }

    private void ReadHex(byte b, long at)
    {
        if (b == (byte)'>')
        {
            if (hexHigh >= 0)
            {
                Emit((byte)(hexHigh << 4), hexOffset);
            }

            hexHigh = -1;
            mode = Mode.Normal;
            return;
        }

        var value = HexValue(b);
        if (value < 0)
        {
            return;
        }

        if (hexHigh < 0)
        {
            hexHigh = value;
            hexOffset = at;
        }
        else
        {
            Emit((byte)((hexHigh << 4) | value), hexOffset);
            hexHigh = -1;
        }
    }

    /// <summary>Reads one byte of a literal string.</summary>
    private void ReadLiteral(byte b, long at)
    {
        switch (escape)
        {
            case Escape.AfterCarriageReturn:
            case Escape.AfterContinuationCarriageReturn:
                escape = Escape.None;
                if (b == Lf)
                {
                    return; // a line end of two bytes counts as one
                }

                break;

            case Escape.Backslash:
                escape = Escape.None;
                switch (b)
                {
                    case (byte)'n':
                        Emit(Lf, escapeOffset);
                        break;
                    case (byte)'r':
                        Emit(Cr, escapeOffset);
                        break;
                    case (byte)'t':
                        Emit(Tab, escapeOffset);
                        break;
                    case (byte)'b':
                        Emit(8, escapeOffset);
                        break;
                    case (byte)'f':
                        Emit(12, escapeOffset);
                        break;
                    case Cr:
                        escape = Escape.AfterContinuationCarriageReturn; // a line continuation: no byte
                        break;
                    case Lf:
                        break;
                    case >= (byte)'0' and <= (byte)'7':
                        octalValue = b - '0';
                        octalDigits = 1;
                        escape = Escape.Octal;
                        break;
                    default:
                        Emit(b, escapeOffset);
                        break;
                }

                return;

            case Escape.Octal:
                if (b is >= (byte)'0' and <= (byte)'7')
                {
                    octalValue = (octalValue << 3) | (b - '0');
                    octalDigits++;
                    if (octalDigits == 3)
                    {
                        Emit((byte)octalValue, escapeOffset);
                        escape = Escape.None;
                    }

                    return;
                }

                Emit((byte)octalValue, escapeOffset);
                escape = Escape.None;
                break;

            default:
                break;
        }

        switch (b)
        {
            case (byte)'\\':
                escape = Escape.Backslash;
                escapeOffset = at;
                break;
            case (byte)'(':
                parenDepth++;
                Emit(b, at);
                break;
            case (byte)')':
                if (--parenDepth == 0)
                {
                    mode = Mode.Normal;
                }
                else
                {
                    Emit(b, at);
                }

                break;
            case Cr:
                Emit(Lf, at);
                escape = Escape.AfterCarriageReturn;
                break;
            default:
                Emit(b, at);
                break;
        }
    }

    private void Emit(byte value, long at)
    {
        stringChannel?.Push(value, at);
        if (arrayDepth > 0)
        {
            arrayChannel?.Push(value, at);
        }

        if (textObject)
        {
            textChannel?.Push(value, at);
        }
    }

    private void EndName()
    {
        var length = Math.Min(nameLength, NameCapacity);
        var decoded = 0;
        for (var i = 0; i < length; i++)
        {
            var value = name[i];
            var source = nameOffsets[i];
            if (value == (byte)'#' && i + 2 < length && HexValue(name[i + 1]) >= 0 && HexValue(name[i + 2]) >= 0)
            {
                value = (byte)((HexValue(name[i + 1]) << 4) | HexValue(name[i + 2]));
                i += 2;
            }

            decodedName[decoded] = value;
            decodedNameOffsets[decoded] = source;
            decoded++;
        }

        var span = decodedName.AsSpan(0, decoded);
        tally.OnName(span, previousNameWasType);
        if (dictDepth is > 0 and <= DictFlagDepth)
        {
            if (span.SequenceEqual("Title"u8))
            {
                dictFlags[dictDepth - 1] |= 1;
            }
            else if (span.SequenceEqual("Parent"u8))
            {
                dictFlags[dictDepth - 1] |= 2;
            }
        }

        if (nameChannel is not null && decoded > 0)
        {
            nameChannel.Reset();
            for (var i = 0; i < decoded; i++)
            {
                nameChannel.Push(decodedName[i], decodedNameOffsets[i]);
            }
        }

        previousNameWasType = span.SequenceEqual("Type"u8);
        nameLength = 0;
    }
}
