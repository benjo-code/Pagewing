using System.Buffers.Binary;
using System.Text;
using Pagewing.Core.Printing;

namespace Pagewing.App.Platform.Windows;

/// <summary>Reads the parts of <c>DEVMODEW</c> and <c>DEVNAMES</c> that Pagewing records from the print dialog.</summary>
internal static class DevMode
{
    private const int FieldsOffset = 72;
    private const int OrientationOffset = 76;
    private const int DuplexOffset = 94;
    private const int MinimumSize = 96;
    private const int OrientationField = 0x1;
    private const int DuplexField = 0x1000;

    /// <summary>Reads the double-sided setting and the orientation of a <c>DEVMODEW</c>; settings the driver did not fill in stay at the default.</summary>
    public static (PrintDuplex Duplex, PrintOrientation Orientation) Read(ReadOnlySpan<byte> devMode)
    {
        if (devMode.Length < MinimumSize)
        {
            return (PrintDuplex.Default, PrintOrientation.Default);
        }

        var fields = BinaryPrimitives.ReadUInt32LittleEndian(devMode[FieldsOffset..]);
        var duplex = PrintDuplex.Default;
        if ((fields & DuplexField) != 0)
        {
            duplex = BinaryPrimitives.ReadInt16LittleEndian(devMode[DuplexOffset..]) switch
            {
                1 => PrintDuplex.Off,
                2 => PrintDuplex.LongEdge,
                3 => PrintDuplex.ShortEdge,
                _ => PrintDuplex.Default,
            };
        }

        var orientation = PrintOrientation.Default;
        if ((fields & OrientationField) != 0)
        {
            orientation = BinaryPrimitives.ReadInt16LittleEndian(devMode[OrientationOffset..]) switch
            {
                1 => PrintOrientation.Portrait,
                2 => PrintOrientation.Landscape,
                _ => PrintOrientation.Default,
            };
        }

        return (duplex, orientation);
    }

    /// <summary>Reads the name of the printer from a <c>DEVNAMES</c> block, or <see langword="null"/> when the block is malformed.</summary>
    public static string? ReadPrinterName(ReadOnlySpan<byte> devNames)
    {
        // wDriverOffset, wDeviceOffset, wOutputOffset, wDefault; the offsets count UTF-16 characters from the start.
        if (devNames.Length < 8)
        {
            return null;
        }

        var start = BinaryPrimitives.ReadUInt16LittleEndian(devNames[2..]) * 2;
        if (start < 8 || start >= devNames.Length)
        {
            return null;
        }

        var text = devNames[start..];
        var length = 0;
        while (length + 1 < text.Length && (text[length] != 0 || text[length + 1] != 0))
        {
            length += 2;
        }

        return Encoding.Unicode.GetString(text[..length]);
    }
}
