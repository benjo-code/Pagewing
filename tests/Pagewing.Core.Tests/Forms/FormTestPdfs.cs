using System.Globalization;
using System.Text;
using Pagewing.Core.Tests.Annotations;

namespace Pagewing.Core.Tests.Forms;

/// <summary>Synthetic form PDFs built inside the tests (no checked-in fixture): large forms and forms with actions.</summary>
internal static class FormTestPdfs
{
    private const string Da = "/DA (/Helv 10 Tf 0 g)";
    private const string Look = "/F 4 /MK << /BC [0 0 0] /BG [1 1 1] >>";

    /// <summary>
    /// A form of <paramref name="pages"/> pages with <paramref name="fieldsPerPage"/> text fields each (<c>F{page}_{n}</c>, no
    /// appearance), plus one field <c>Shared</c> with a widget on each of the first <paramref name="sharedPages"/> pages.
    /// </summary>
    public static string WriteLargeForm(TempFolder folder, string name, int pages, int fieldsPerPage, int sharedPages = 0)
    {
        // 1 catalog, 2 pages, 3 font, 4 the parent of Shared, then pages and widgets.
        var objects = new List<string> { string.Empty, string.Empty, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", string.Empty };
        var pageIds = new List<int>();
        var fields = new List<int>();
        var sharedKids = new List<int>();
        for (var page = 0; page < pages; page++)
        {
            objects.Add(string.Empty);
            var pageId = objects.Count;
            pageIds.Add(pageId);
            var annots = new List<int>();
            for (var n = 0; n < fieldsPerPage; n++)
            {
                var x = 20 + ((n % 4) * 145);
                var y = 760 - ((n / 4) * 28);
                objects.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"<< /Type /Annot /Subtype /Widget /Rect [{x} {y - 24} {x + 140} {y}] /P {pageId} 0 R {Look} /FT /Tx /T (F{page}_{n}) {Da} >>"));
                annots.Add(objects.Count);
                fields.Add(objects.Count);
            }

            if (page < sharedPages)
            {
                objects.Add(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Annot /Subtype /Widget /Rect [20 20 160 44] /P {pageId} 0 R {Look} /Parent 4 0 R >>"));
                annots.Add(objects.Count);
                sharedKids.Add(objects.Count);
            }

            objects[pageId - 1] = string.Create(
                CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [{Refs(annots)}] >>");
        }

        if (sharedKids.Count > 0)
        {
            fields.Add(4);
        }

        objects[3] = $"<< /FT /Tx /T (Shared) /V (Geteilt) {Da} /Kids [{Refs(sharedKids)}] >>";
        objects[0] = $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [{Refs(fields)}] {Da} /DR << /Font << /Helv 3 0 R >> >> >> >>";
        objects[1] = string.Create(CultureInfo.InvariantCulture, $"<< /Type /Pages /Kids [{Refs(pageIds)}] /Count {pages} >>");
        return AnnotationTestFiles.WritePdf(folder, name, objects);
    }

    /// <summary>
    /// One page with a text field <c>Other</c> (value <c>Wert</c>, default <c>Vorgabe</c>), a check box <c>Box</c> whose
    /// mouse-up runs <c>/ResetForm</c>, and a text field <c>Hider</c> whose focus runs <c>/Hide</c> on <c>Other</c>. With
    /// <paramref name="otherOnSecondPage"/>, <c>Other</c> sits alone on a second page.
    /// </summary>
    public static string WriteActionForm(TempFolder folder, string name, bool otherOnSecondPage = false)
    {
        var otherPage = otherOnSecondPage ? 10 : 3;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 9 0 R >> >> >> >>",
            otherOnSecondPage ? "<< /Type /Pages /Kids [3 0 R 10 0 R] /Count 2 >>" : "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            otherOnSecondPage
                ? "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [5 0 R 6 0 R] >>"
                : "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R 6 0 R] >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [20 240 280 262] /P {otherPage} 0 R {Look} /FT /Tx /T (Other) /V (Wert) /DV (Vorgabe) {Da} >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [20 200 38 218] /P 3 0 R {Look} /FT /Btn /T (Box) /V /Off /AS /Off /AP << /N << /Yes 7 0 R /Off 8 0 R >> >> /AA << /U << /S /ResetForm >> >> >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [20 160 280 182] /P 3 0 R {Look} /FT /Tx /T (Hider) {Da} /AA << /Fo << /S /Hide /T (Other) /H true >> >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 18 18]", "0 g 4 4 10 10 re f"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 18 18]", string.Empty),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (otherOnSecondPage)
        {
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R] >>");
        }

        return AnnotationTestFiles.WritePdf(folder, name, objects);
    }

    /// <summary>
    /// One page with a check box <c>Box</c> of two widgets with the export values <c>A</c> and <c>B</c>, the second one on
    /// (<c>/V /B</c>).
    /// </summary>
    public static string WriteTwoWidgetCheckBox(TempFolder folder, string name)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 9 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [5 0 R 6 0 R] >>",
            "<< /FT /Btn /T (Box) /V /B /Kids [5 0 R 6 0 R] >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [20 200 38 218] /P 3 0 R {Look} /Parent 4 0 R /AS /Off /AP << /N << /A 7 0 R /Off 8 0 R >> >> >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [60 200 78 218] /P 3 0 R {Look} /Parent 4 0 R /AS /B /AP << /N << /B 7 0 R /Off 8 0 R >> >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 18 18]", "0 g 4 4 10 10 re f"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 18 18]", string.Empty),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        return AnnotationTestFiles.WritePdf(folder, name, objects);
    }

    private static string Stream(string dictionary, string content) =>
        string.Create(CultureInfo.InvariantCulture, $"<< {dictionary} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream");

    private static string Refs(IEnumerable<int> ids) => string.Join(' ', ids.Select(id => string.Create(CultureInfo.InvariantCulture, $"{id} 0 R")));
}
