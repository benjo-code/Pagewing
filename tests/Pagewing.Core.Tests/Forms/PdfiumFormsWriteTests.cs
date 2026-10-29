using Pagewing.Pdfium;
using Xunit;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// The font check of the read-back (milestone 11, task 7): the fonts a content stream selects with <c>Tf</c>, read from the
/// appearance PDFium's engine writes and from <c>/DA</c>, without being fooled by strings or comments.
/// </summary>
public sealed class PdfiumFormsWriteTests
{
    [Fact]
    public void FontsOfContentNamesEveryFontOfATfOperatorOnce()
    {
        Assert.Equal(["Helv"], PdfiumForms.FontsOfContent("/Tx BMC q BT /Helv 10 Tf 0 g 2 6.5 Td (Hallo) Tj ET Q EMC"));
        Assert.Equal(
            ["Helv", "Tahoma_00"],
            PdfiumForms.FontsOfContent("BT /Helv 10 Tf (a) Tj /Tahoma_00 10 Tf <0102> Tj /Helv 12.5 Tf (b) Tj ET"));
        Assert.Equal(["Helv"], PdfiumForms.FontsOfContent("/Helv 0 Tf 0 g"));
        Assert.Equal(["F1", "F2"], PdfiumForms.FontsOfContent("/F1 10 Tf\r\n/F2\t9 Tf"));
        Assert.Empty(PdfiumForms.FontsOfContent(string.Empty));
        Assert.Empty(PdfiumForms.FontsOfContent("/Tx BMC EMC"));
    }

    [Fact]
    public void FontsOfContentIgnoresStringsCommentsAndOtherOperators()
    {
        // Text a user typed ends up in strings; a name in it is no font.
        Assert.Equal(["Helv"], PdfiumForms.FontsOfContent("BT /Helv 10 Tf (x /Evil 1 Tf \\) /Evil2 1 Tf) Tj ET"));
        Assert.Equal(["Helv"], PdfiumForms.FontsOfContent("BT /Helv 10 Tf (a ( /Nested 1 Tf ) b) Tj ET"));
        Assert.Equal(["Helv"], PdfiumForms.FontsOfContent("BT /Helv 10 Tf <2F45 76696C2031 205466> Tj ET"));
        Assert.Equal(["Helv"], PdfiumForms.FontsOfContent("% /Comment 1 Tf\nBT /Helv 10 Tf ET"));

        // A name before another operator or without a size is no font selection.
        Assert.Equal(["Helv"], PdfiumForms.FontsOfContent("/GS0 gs /Helv 10 Tf /Name Tf"));
        Assert.Equal(["Helv"], PdfiumForms.FontsOfContent("/Bad 10 Tfx /Helv 10 Tf"));
    }
}
