using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Pagewing.TestSupport;

/// <summary>
/// Minimal PNG decoder for tests: 8-bit RGB (colour type 2) and RGBA (colour type 6), filters 0 to 4, not interlaced,
/// any number of IDAT chunks. Signature and chunk CRCs are checked. Alpha is dropped.
/// </summary>
internal static class PngDecoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes a PNG into its size and tightly packed RGB bytes (top row first).</summary>
    /// <exception cref="InvalidDataException">The data is not a well-formed PNG (signature, chunk layout, CRC, content).</exception>
    /// <exception cref="NotSupportedException">The PNG uses a feature outside the supported subset.</exception>
    public static (int Width, int Height, byte[] Rgb) Decode(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length < Signature.Length || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
        {
            throw new InvalidDataException("The data does not start with the PNG signature.");
        }

        var width = 0;
        var height = 0;
        var colorType = -1;
        var seenHeader = false;
        var seenEnd = false;
        using var idat = new MemoryStream();

        var offset = Signature.Length;
        while (!seenEnd)
        {
            if (offset + 12 > png.Length)
            {
                throw new InvalidDataException("The PNG ends in the middle of a chunk (no IEND).");
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset, 4));
            if (length < 0 || offset + 12L + length > png.Length)
            {
                throw new InvalidDataException($"Chunk length {length} at offset {offset} exceeds the data.");
            }

            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            var stored = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length, 4));
            var actual = Crc32(png.AsSpan(offset + 4, 4 + length));
            if (stored != actual)
            {
                throw new InvalidDataException($"CRC mismatch in chunk {type} at offset {offset}.");
            }

            switch (type)
            {
                case "IHDR":
                    if (length != 13)
                    {
                        throw new InvalidDataException($"IHDR has length {length}, expected 13.");
                    }

                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data.Slice(4, 4));
                    colorType = data[9];
                    if (width <= 0 || height <= 0)
                    {
                        throw new InvalidDataException($"Invalid image size {width}x{height}.");
                    }

                    if (data[8] != 8)
                    {
                        throw new NotSupportedException($"Bit depth {data[8]} is not supported (only 8).");
                    }

                    if (colorType is not (2 or 6))
                    {
                        throw new NotSupportedException($"Colour type {colorType} is not supported (only 2 and 6).");
                    }

                    if (data[10] != 0 || data[11] != 0)
                    {
                        throw new InvalidDataException("Unknown compression or filter method.");
                    }

                    if (data[12] != 0)
                    {
                        throw new NotSupportedException("Interlaced PNGs are not supported.");
                    }

                    seenHeader = true;
                    break;
                case "IDAT":
                    if (!seenHeader)
                    {
                        throw new InvalidDataException("IDAT before IHDR.");
                    }

                    idat.Write(data);
                    break;
                case "IEND":
                    seenEnd = true;
                    break;
                default:
                    if ((type[0] & 0x20) == 0)
                    {
                        throw new NotSupportedException($"Critical chunk {type} is not supported.");
                    }

                    break;
            }

            offset += 12 + length;
        }

        if (!seenHeader)
        {
            throw new InvalidDataException("The PNG has no IHDR chunk.");
        }

        return (width, height, Unfilter(idat.ToArray(), width, height, colorType == 2 ? 3 : 4));
    }

    private static byte[] Unfilter(byte[] compressed, int width, int height, int bytesPerPixel)
    {
        var rowLength = checked(width * bytesPerPixel);
        var expected = checked((long)height * (rowLength + 1));
        var raw = new byte[expected];
        using (var zlib = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            try
            {
                zlib.ReadExactly(raw);
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("The image data is shorter than the header announces.", exception);
            }

            if (zlib.ReadByte() != -1)
            {
                throw new InvalidDataException("The image data is longer than the header announces.");
            }
        }

        var rgb = new byte[width * height * 3];
        var previous = new byte[rowLength];
        var current = new byte[rowLength];
        for (var y = 0; y < height; y++)
        {
            var start = y * (rowLength + 1);
            var filter = raw[start];
            for (var i = 0; i < rowLength; i++)
            {
                int x = raw[start + 1 + i];
                int a = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                int b = previous[i];
                int c = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                current[i] = filter switch
                {
                    0 => (byte)x,
                    1 => (byte)(x + a),
                    2 => (byte)(x + b),
                    3 => (byte)(x + ((a + b) / 2)),
                    4 => (byte)(x + Paeth(a, b, c)),
                    _ => throw new InvalidDataException($"Unknown filter type {filter} in row {y}."),
                };
            }

            for (var px = 0; px < width; px++)
            {
                Array.Copy(current, px * bytesPerPixel, rgb, ((y * width) + px) * 3, 3);
            }

            (current, previous) = (previous, current);
        }

        return rgb;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
