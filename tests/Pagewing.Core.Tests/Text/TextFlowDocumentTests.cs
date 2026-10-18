using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Text;

public sealed class TextFlowDocumentTests
{
    private readonly PdfEngine engine = new();

    [Fact]
    public async Task RichDocumentCopiesAsProse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);
        var text = await document.GetPageTextAsync(0, ct);
        var selection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, text.Chars.Count));
        var slices = TextFlow.SlicesOf(selection, _ => text);

        var plain = TextFlow.Plain(slices);
        var html = TextFlow.Html(slices);
        var raw = TextFlow.Raw(slices);

        // Heading -> paragraph 26 pt apart, paragraph -> paragraph 20 pt, lines within a paragraph 4 pt (em boxes).
        Assert.StartsWith("Pagewing Handbuch\n\nPagewing ist ein schneller PDF-Viewer für Windows und Linux. Er startet sofort und bleibt auch bei großen Dokumenten flüssig.\n\nDas ist fett und kursiv im Satz. Zweite Zeile des zweiten Absatzes.", plain, StringComparison.Ordinal);
        Assert.Contains("<h1>Pagewing Handbuch</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<b>fett</b>", html, StringComparison.Ordinal);
        Assert.Contains("<i>kursiv</i>", html, StringComparison.Ordinal);
        Assert.StartsWith("Pagewing Handbuch\nPagewing ist ein schneller PDF-Viewer für Win-\ndows und Linux.", raw, StringComparison.Ordinal);
    }
}
