using System.Buffers.Binary;

namespace Pagewing.Core.Signatures;

/// <summary>Reads the size of a PNG or JPEG file from its head, so that nothing has to be decoded to refuse a huge image.</summary>
public static class ImageHeader
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Reads the pixel size: from the IHDR chunk of a PNG, from the first frame header (SOF0 to SOF15, without the table markers
    /// DHT, JPG and DAC) of a JPEG.
    /// </summary>
    /// <returns><see langword="false"/> for any other file, a truncated head, a size of zero or one beyond the range of an <see cref="int"/>.</returns>
    public static bool TryReadSize(ReadOnlySpan<byte> file, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (file.Length >= 24 && file[..8].SequenceEqual(PngSignature))
        {
            return file.Slice(12, 4).SequenceEqual("IHDR"u8)
                && TryTake(BinaryPrimitives.ReadUInt32BigEndian(file[16..]), BinaryPrimitives.ReadUInt32BigEndian(file[20..]), out width, out height);
        }

        return file.Length >= 4 && file[0] == 0xFF && file[1] == 0xD8 && TryReadJpeg(file, out width, out height);
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> file, out int width, out int height)
    {
        width = 0;
        height = 0;
        var position = 2;
        while (position < file.Length)
        {
            if (file[position] != 0xFF)
            {
                return false;
            }

            while (position < file.Length && file[position] == 0xFF)
            {
                position++;
            }

            if (position >= file.Length)
            {
                return false;
            }

            var marker = file[position++];
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8))
            {
                // Stuffed zero, TEM, RSTn, SOI: no length follows (a stuffed zero outside the data is damage, skipping it is harmless).
                continue;
            }

            if (marker is 0xD9 or 0xDA || position + 2 > file.Length)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(file[position..]);
            if (length < 2)
            {
                return false;
            }

            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                // Length (2), precision (1), height (2), width (2).
                return length >= 8 && position + 7 <= file.Length
                    && TryTake(BinaryPrimitives.ReadUInt16BigEndian(file[(position + 5)..]), BinaryPrimitives.ReadUInt16BigEndian(file[(position + 3)..]), out width, out height);
            }

            position += length;
        }

        return false;
    }

    private static bool TryTake(uint widthValue, uint heightValue, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (widthValue == 0 || heightValue == 0 || widthValue > int.MaxValue || heightValue > int.MaxValue)
        {
            return false;
        }

        width = (int)widthValue;
        height = (int)heightValue;
        return true;
    }
}
