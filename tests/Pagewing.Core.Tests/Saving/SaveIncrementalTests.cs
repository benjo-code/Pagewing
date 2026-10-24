using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Saving;

/// <summary><see cref="PdfDocument.SaveIncrementalAsync"/> on its own, without the save pipeline.</summary>
public sealed class SaveIncrementalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnchangedDocumentWritesTheOriginalBytesToAMemoryStream()
    {
        var path = TestData.Pdf("text-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, Ct);
        var document = await new PdfEngine().OpenAsync(path, cancellationToken: Ct);
        using var stream = new MemoryStream();

        await document.SaveIncrementalAsync(stream, Ct);

        var saved = stream.ToArray();
        Assert.True(saved.Length >= original.Length);
        Assert.Equal(original, saved.AsSpan(0, original.Length).ToArray());

        await document.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => { _ = document.SaveIncrementalAsync(new MemoryStream(), Ct); });
    }

    [Fact]
    public async Task SavedMarkupReopensAndIsListed()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "saved.pdf");
        await using (var document = await new PdfEngine().OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: Ct))
        {
            await document.CreateMarkupAsync(0, AnnotationKind.Underline, [new PageRect(20, 20, 120, 40)], AnnotationTestFiles.Blue, "Note", null, Ct);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            await document.SaveIncrementalAsync(stream, Ct);
        }

        await using var reopened = await new PdfEngine().OpenAsync(path, cancellationToken: Ct);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, Ct));
        Assert.Equal(AnnotationKind.Underline, annotation.Kind);
        Assert.Equal("Note", annotation.Contents);
    }
}
