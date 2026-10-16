using System.Buffers.Binary;
using System.IO.Compression;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Imaging;

/// <summary>
/// Encodes a <see cref="RenderedImage"/> as an 8-bit RGB PNG (colour type 2, not interlaced). The alpha channel is dropped.
/// Rows use the "Up" filter, which turns the large flat areas of a page into runs of zeros and keeps a white A4 page tiny.
/// </summary>
public static class PngEncoder
{
    private const int MaxIdatLength = 64 * 1024;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IhdrType = "IHDR"u8.ToArray();
    private static readonly byte[] IdatType = "IDAT"u8.ToArray();
    private static readonly byte[] IendType = "IEND"u8.ToArray();
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes the image and returns the PNG file.</summary>
    public static byte[] Encode(RenderedImage image)
    {
        using var stream = new MemoryStream();
        Encode(image, stream);
        return stream.ToArray();
    }

    /// <summary>Encodes the image into <paramref name="destination"/>; the stream stays open.</summary>
    public static void Encode(RenderedImage image, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(destination);

        destination.Write(Signature);
        WriteHeader(destination, image);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            WriteFilteredRows(zlib, image);
        }

        var buffer = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        for (var offset = 0; offset < buffer.Length; offset += MaxIdatLength)
        {
            WriteChunk(destination, IdatType, buffer.Slice(offset, Math.Min(MaxIdatLength, buffer.Length - offset)));
        }

        WriteChunk(destination, IendType, []);
    }

    private static void WriteHeader(Stream destination, RenderedImage image)
    {
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[..4], image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4, 4), image.Height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type: truecolour (RGB)
        ihdr[10] = 0; // compression method: deflate
        ihdr[11] = 0; // filter method: adaptive
        ihdr[12] = 0; // interlace method: none
        WriteChunk(destination, IhdrType, ihdr);
    }

    /// <summary>Writes every scanline as filter byte 2 (Up) followed by the RGB bytes minus those of the row above.</summary>
    private static void WriteFilteredRows(Stream destination, RenderedImage image)
    {
        var width = image.Width;
        var rowLength = width * 3;
        var source = image.Pixels.Span;
        var current = new byte[rowLength];
        var previous = new byte[rowLength];
        var output = new byte[1 + rowLength];
        output[0] = 2;

        for (var y = 0; y < image.Height; y++)
        {
            var row = source.Slice(y * image.Stride, width * 4);
            for (int x = 0, s = 0, d = 0; x < width; x++, s += 4, d += 3)
            {
                current[d] = row[s + 2];
                current[d + 1] = row[s + 1];
                current[d + 2] = row[s];
            }

            for (var i = 0; i < rowLength; i++)
            {
                output[1 + i] = (byte)(current[i] - previous[i]);
            }

            destination.Write(output);
            (current, previous) = (previous, current);
        }
    }

    private static void WriteChunk(Stream destination, byte[] type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        destination.Write(number);
        destination.Write(type);
        destination.Write(data);

        var crc = Crc32(0xFFFFFFFFu, type);
        crc = Crc32(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        destination.Write(number);
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
