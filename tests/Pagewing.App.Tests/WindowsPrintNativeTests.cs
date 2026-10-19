using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Pagewing.App.Platform.Windows;
using Pagewing.Core.Printing;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class WindowsPrintNativeTests
{
    [Fact]
    public void PrintDlgExHasTheLayoutOfPrintDlgExW()
    {
        // commdlg.h, 64-bit: 136 bytes, dwResultAction at 132, lpPageRanges at 64, lphPropertyPages at 120.
        Assert.Equal(136, Unsafe.SizeOf<PrintDlgEx>());
        Assert.Equal(132, (int)Marshal.OffsetOf<PrintDlgEx>(nameof(PrintDlgEx.ResultAction)));
        Assert.Equal(64, (int)Marshal.OffsetOf<PrintDlgEx>(nameof(PrintDlgEx.PageRanges)));
        Assert.Equal(120, (int)Marshal.OffsetOf<PrintDlgEx>(nameof(PrintDlgEx.PropertyPages)));
        Assert.Equal(48, (int)Marshal.OffsetOf<PrintDlgEx>(nameof(PrintDlgEx.ExclusionFlags)));
        Assert.Equal(8, Unsafe.SizeOf<PrintPageRange>());
    }

    [Fact]
    public void DocInfoHasTheLayoutOfDocInfoW()
    {
        // wingdi.h, 64-bit: cbSize at 0, lpszDocName at 8, lpszOutput at 16, lpszDatatype at 24, fwType at 32; 40 bytes.
        Assert.Equal(40, Unsafe.SizeOf<DocInfo>());
        Assert.Equal(16, (int)Marshal.OffsetOf<DocInfo>(nameof(DocInfo.Output)));
        Assert.Equal(32, (int)Marshal.OffsetOf<DocInfo>(nameof(DocInfo.Type)));
    }

    [Theory]
    [InlineData(new uint[] { 1, 3 }, "1-3")]
    [InlineData(new uint[] { 2, 2 }, "2")]
    [InlineData(new uint[] { 1, 2, 5, 7 }, "1-2,5-7")]
    [InlineData(new uint[] { 9, 4 }, "4-9")]
    [InlineData(new uint[] { 0, 0, 3, 3 }, "3")]
    public void RangesOfTheDialogBecomeText(uint[] pairs, string expected)
    {
        var ranges = new PrintPageRange[pairs.Length / 2];
        for (var i = 0; i < ranges.Length; i++)
        {
            ranges[i] = new PrintPageRange { FromPage = pairs[2 * i], ToPage = pairs[(2 * i) + 1] };
        }

        Assert.Equal(expected, PrintRangeText.Format(ranges));
    }

    [Fact]
    public void DevModeReadsDuplexAndOrientation()
    {
        var devMode = new byte[220];
        BitConverter.TryWriteBytes(devMode.AsSpan(72), 0x1u | 0x1000u);
        BitConverter.TryWriteBytes(devMode.AsSpan(76), (short)2);
        BitConverter.TryWriteBytes(devMode.AsSpan(94), (short)3);

        var (duplex, orientation) = DevMode.Read(devMode);

        Assert.Equal(PrintDuplex.ShortEdge, duplex);
        Assert.Equal(PrintOrientation.Landscape, orientation);
    }

    [Fact]
    public void DevModeIgnoresFieldsTheDriverDidNotFill()
    {
        var devMode = new byte[220];
        BitConverter.TryWriteBytes(devMode.AsSpan(76), (short)2);
        BitConverter.TryWriteBytes(devMode.AsSpan(94), (short)3);

        Assert.Equal((PrintDuplex.Default, PrintOrientation.Default), DevMode.Read(devMode));
        Assert.Equal((PrintDuplex.Default, PrintOrientation.Default), DevMode.Read(new byte[10]));
    }

    [Fact]
    public void DevNamesGiveThePrinterName()
    {
        // DEVNAMES: four WORDs, then "driver\0printer\0port\0" as UTF-16; the offsets count characters.
        var text = "winspool\0Mein Drucker\0LPT1\0";
        var bytes = new byte[8 + (text.Length * 2)];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (ushort)4);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), (ushort)(4 + "winspool\0".Length));
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (ushort)(4 + "winspool\0Mein Drucker\0".Length));
        System.Text.Encoding.Unicode.GetBytes(text, bytes.AsSpan(8));

        Assert.Equal("Mein Drucker", DevMode.ReadPrinterName(bytes));
        Assert.Null(DevMode.ReadPrinterName(new byte[4]));
    }
}
