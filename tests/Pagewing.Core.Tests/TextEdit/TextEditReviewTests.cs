using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.TextEdit;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>Findings of the review of the whole branch of milestone 16 (Docs/DECISIONS.md, "Text bearbeiten: Befunde der Abschlussprüfung (M16)").</summary>
public sealed class TextEditReviewTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    private string Copy(string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    [Fact]
    public async Task ALineWithALigatureIsLockedBecauseItsPartsShareOneOrigin()
    {
        // The ligature fixture is also the case the copy check was made for (a glyph that stands for "ae", no glyph of its own for "a": the code 0 reads back as
        // another character); the lock comes first, the check on the copy is the second line of defence.
        await using var document = await engine.OpenAsync(Copy("redact-ligature.pdf"), cancellationToken: Ct);
        var runs = await document.GetTextRunsAsync(0, Ct);

        var line = runs.Single(static r => r.Text.Contains("sthetics", StringComparison.Ordinal));

        Assert.Equal(TextEditLockReason.Ligature, line.Lock);
        Assert.Null(runs.Single(static r => r.Text.StartsWith("Ligature", StringComparison.Ordinal)).Lock);
        await Assert.ThrowsAsync<TextRunLockedException>(() => document.ReplaceTextRunAsync(line, line.Text + "!", TextEditMode.KeepFont, Ct));
    }

    private static async Task<string> StateAsync(PdfDocument document)
    {
        using var stream = new MemoryStream();
        await document.SaveIncrementalAsync(stream, Ct);
        return System.Text.RegularExpressions.Regex.Replace(System.Text.Encoding.Latin1.GetString(stream.ToArray()), @"/ID\s*\[<[0-9A-Fa-f]+><[0-9A-Fa-f]+>\]", "/ID[]");
    }
}
