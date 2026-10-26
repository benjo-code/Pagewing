using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Pdfium.Native;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// Review fixes of task 7: a write loads only the pages of its field (found through the name index of the form scan), a field
/// on more than <see cref="PdfDocument.MaxPagesPerField"/> pages is refused, every failure after the write is a
/// <see cref="FormWriteException"/> that says whether the field is as before, and what PDFium's native actions (no JavaScript)
/// do during a write.
/// </summary>
public sealed partial class FormRoundTripTests
{
    [Fact]
    public async Task WriteLoadsOnlyThePagesOfTheField()
    {
        await using var document = await OpenAsync(FormTestPdfs.WriteLargeForm(folder, "large.pdf", pages: 20, fieldsPerPage: 100, sharedPages: 3));
        await document.GetFormInfoAsync(Ct);
        var loaded = new List<int>();
        document.FormFaults = new FormWriteFaults { PageLoaded = loaded.Add };

        var change = await document.SetFormValueAsync("F10_50", FormFieldState.OfText("Zehn"), Ct);
        Assert.Equal([10], loaded);
        Assert.Equal([10], change.Pages.Order());

        loaded.Clear();
        var shared = await document.SetFormValueAsync("Shared", FormFieldState.OfText("Drei"), Ct);
        Assert.Equal([0, 1, 2], loaded);
        Assert.Equal([0, 1, 2], shared.Pages.Order());

        // A name the index does not know loads no page at all.
        loaded.Clear();
        await Assert.ThrowsAsync<FormFieldNotFoundException>(() => document.SetFormValueAsync("F99_1", FormFieldState.OfText("x"), Ct));
        Assert.Empty(loaded);
        Assert.Equal(FormFieldState.OfText("Zehn"), (await FieldAsync(document, "F10_50")).State);
    }

    [Fact]
    public async Task WriteWithoutACompleteNameIndexFindsTheFieldByItsName()
    {
        await using var document = await OpenAsync(FormTestPdfs.WriteLargeForm(folder, "large.pdf", pages: 5, fieldsPerPage: 10, sharedPages: 2));

        // The index keeps too few characters for this form: the write reads the names of every page with fields instead.
        document.FormNameIndexLimit = 10;
        await document.GetFormInfoAsync(Ct);
        var loaded = new List<int>();
        document.FormFaults = new FormWriteFaults { PageLoaded = loaded.Add };

        var change = await document.SetFormValueAsync("Shared", FormFieldState.OfText("Zwei"), Ct);
        Assert.Equal([0, 1], loaded);
        Assert.Equal([0, 1], change.Pages.Order());
        Assert.Equal(FormFieldState.OfText("Zwei"), change.After);
        await Assert.ThrowsAsync<FormFieldNotFoundException>(() => document.SetFormValueAsync("Nirgends", FormFieldState.OfText("x"), Ct));
    }

    [Fact]
    public async Task FieldOnMoreThanMaxPagesIsRefusedAndLocked()
    {
        var pages = PdfDocument.MaxPagesPerField + 1;
        await using var document = await OpenAsync(FormTestPdfs.WriteLargeForm(folder, "many.pdf", pages, fieldsPerPage: 1, sharedPages: pages));
        var loaded = new List<int>();
        document.FormFaults = new FormWriteFaults { PageLoaded = loaded.Add };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => document.SetFormValueAsync("Shared", FormFieldState.OfText("x"), Ct));
        Assert.Contains("pages", error.Message, StringComparison.Ordinal);
        Assert.Empty(loaded);

        // After the scan the reading reports the lock as well; the other fields stay fillable.
        var field = (await document.GetFormFieldsAsync(0, Ct)).Single(f => f.Key.FieldName == "Shared");
        Assert.Equal(FormLock.Unsupported, field.Lock);
        Assert.Contains("pages", field.LockReason, StringComparison.Ordinal);
        Assert.Equal(FormFieldState.OfText("Geteilt"), field.State);
        Assert.Equal(FormLock.None, (await document.GetFormFieldsAsync(0, Ct)).Single(f => f.Key.FieldName == "F0_0").Lock);
    }

    [Fact]
    public async Task FieldOnMaxPagesIsWritten()
    {
        var pages = PdfDocument.MaxPagesPerField;
        await using var document = await OpenAsync(FormTestPdfs.WriteLargeForm(folder, "max.pdf", pages, fieldsPerPage: 1, sharedPages: pages));

        var change = await document.SetFormValueAsync("Shared", FormFieldState.OfText("Alle"), Ct);

        Assert.Equal(Enumerable.Range(0, pages), change.Pages.Order());
        Assert.Equal(FormFieldState.OfText("Alle"), change.After);
    }

    [Fact]
    public async Task ReadBackFailureIsRestoredAndReported()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);
        document.FormFaults = new FormWriteFaults { ReadBackThrows = true };

        var error = await Assert.ThrowsAsync<FormWriteException>(() => document.SetFormValueAsync("Name", FormFieldState.OfText("Neu"), Ct));

        Assert.True(error.Restored);
        Assert.Equal([0, 1], error.Pages.Order());
        Assert.IsType<InvalidOperationException>(error.InnerException);
        document.FormFaults = null;
        Assert.All(await WidgetsAsync(document, "Name"), f => Assert.Equal(FormFieldState.OfText("Alt"), f.State));
    }

    [Fact]
    public async Task FailedRestoreIsReportedAsNotRestored()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);
        document.FormFaults = new FormWriteFaults { ReadBackThrows = true, RestoreThrows = true };

        var error = await Assert.ThrowsAsync<FormWriteException>(() => document.SetFormValueAsync("Telefon", FormFieldState.OfText("Neu"), Ct));

        // The document may have changed: the caller must redraw the pages and must not take the field as unchanged.
        Assert.False(error.Restored);
        Assert.Equal([1], error.Pages.Order());
        document.FormFaults = null;
        Assert.Equal(FormFieldState.OfText("Neu"), (await FieldAsync(document, "Telefon")).State);
    }

    [Fact]
    public async Task RefusalsBeforeTheWriteAreNoFormWriteException()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => document.SetFormValueAsync("Gesperrt", FormFieldState.OfText("x"), Ct));
        var radio = await Assert.ThrowsAsync<InvalidOperationException>(() => document.SetFormValueAsync("Zahlung", FormFieldState.OfText(null), Ct));
        Assert.IsNotType<FormWriteException>(locked);
        Assert.IsNotType<FormWriteException>(radio);
    }

    [Fact]
    public async Task CheckBoxWithTwoExportValuesGoesOffAtTheCheckedWidget()
    {
        await using var document = await OpenAsync(FormTestPdfs.WriteTwoWidgetCheckBox(folder, "box.pdf"));
        Assert.Equal("-x", await CheckedWidgetsAsync(document, "Box"));

        var off = await document.SetFormValueAsync("Box", FormFieldState.OfChecked(false), Ct);
        Assert.Equal(FormFieldState.OfChecked(true), off.Before);
        Assert.Equal(FormFieldState.OfChecked(false), off.After);
        Assert.Equal("--", await CheckedWidgetsAsync(document, "Box"));

        var on = await document.SetFormValueAsync("Box", FormFieldState.OfChecked(true), Ct);
        Assert.Equal(FormFieldState.OfChecked(true), on.After);
        Assert.Equal("x-", await CheckedWidgetsAsync(document, "Box"));
    }

    [Fact]
    public async Task RestoreBringsBackTheCheckedWidgetNotJustTheCheckedState()
    {
        await using var document = await OpenAsync(FormTestPdfs.WriteTwoWidgetCheckBox(folder, "box.pdf"));
        document.FormFaults = new FormWriteFaults { ReadBackThrows = true };

        var error = await Assert.ThrowsAsync<FormWriteException>(() => document.SetFormValueAsync("Box", FormFieldState.OfChecked(false), Ct));

        Assert.True(error.Restored);
        document.FormFaults = null;
        Assert.Equal("-x", await CheckedWidgetsAsync(document, "Box"));
    }

    /// <summary>
    /// Pin: a check box with <c>/AA /U /ResetForm</c> runs the reset natively when it is clicked (no JavaScript involved). The
    /// other field goes back to its <c>/DV</c> with a new appearance; the write of the check box is accepted. The change of the
    /// other field is not in <see cref="FormChange.Pages"/> (here the same page).
    /// </summary>
    [Fact]
    public async Task NativeResetFormActionRunsOnClickAndIsAccepted()
    {
        await using var document = await OpenAsync(FormTestPdfs.WriteActionForm(folder, "reset.pdf"));
        Assert.Equal(FormFieldState.OfText("Wert"), (await FieldAsync(document, "Other")).State);

        var change = await document.SetFormValueAsync("Box", FormFieldState.OfChecked(true), Ct);

        Assert.Equal(FormFieldState.OfChecked(true), change.After);
        Assert.Equal([0], change.Pages.Order());
        Assert.Equal(FormFieldState.OfText("Vorgabe"), (await FieldAsync(document, "Other")).State);
    }

    /// <summary>
    /// Pin: the same reset with the other field on a page the write did not load changes its <c>/V</c> but not its appearance
    /// (the engine reaches only widgets of loaded pages), and that page is not in <see cref="FormChange.Pages"/>.
    /// </summary>
    [Fact]
    public async Task NativeResetFormActionOnAnotherPageChangesTheValueButNotTheAppearance()
    {
        await using var untouched = await OpenAsync(FormTestPdfs.WriteActionForm(folder, "plain.pdf", otherOnSecondPage: true));
        var before = await RenderWidgetsAsync(untouched, 1);
        await using var document = await OpenAsync(FormTestPdfs.WriteActionForm(folder, "reset.pdf", otherOnSecondPage: true));

        var change = await document.SetFormValueAsync("Box", FormFieldState.OfChecked(true), Ct);

        Assert.Equal([0], change.Pages.Order());
        var other = await FieldAsync(document, "Other");
        Assert.Equal(FormFieldState.OfText("Vorgabe"), other.State);
        AssertSamePicture(before, other.Bounds, await RenderWidgetsAsync(document, 1), other.Bounds);
    }

    /// <summary>
    /// Pin: a text field with <c>/AA /Fo /Hide</c> hides the named field natively when the write focuses it: the other
    /// widget gets the flag Hidden (<c>/F</c> 4 becomes 6). The write itself is accepted.
    /// </summary>
    [Fact]
    public async Task NativeHideActionRunsOnFocusAndIsAccepted()
    {
        await using var document = await OpenAsync(FormTestPdfs.WriteActionForm(folder, "hide.pdf"));
        Assert.Equal(FpdfConstants.AnnotFlagPrint, await WidgetFlagsAsync(document, 0, "Other"));

        var change = await document.SetFormValueAsync("Hider", FormFieldState.OfText("x"), Ct);

        Assert.Equal(FormFieldState.OfText("x"), change.After);
        Assert.Equal(FpdfConstants.AnnotFlagPrint | FpdfConstants.AnnotFlagHidden, await WidgetFlagsAsync(document, 0, "Other"));
    }

    /// <summary>Pin: the hide action does not reach a widget on a page the write did not load.</summary>
    [Fact]
    public async Task NativeHideActionDoesNotReachAnUnloadedPage()
    {
        await using var document = await OpenAsync(FormTestPdfs.WriteActionForm(folder, "hide.pdf", otherOnSecondPage: true));

        await document.SetFormValueAsync("Hider", FormFieldState.OfText("x"), Ct);

        Assert.Equal(FpdfConstants.AnnotFlagPrint, await WidgetFlagsAsync(document, 1, "Other"));
    }

    /// <summary>The annotation flags (<c>/F</c>) of the first widget of a field on a page.</summary>
    private static async Task<int> WidgetFlagsAsync(PdfDocument document, int pageIndex, string name)
    {
        var index = (await document.GetFormFieldsAsync(pageIndex, Ct)).First(f => f.Key.FieldName == name).Index;
        var native = PdfiumOf(document);
        return await native.Thread.InvokeAsync(
            () =>
            {
                using var page = native.LoadPage(pageIndex);
                var annotation = Fpdf.FPDFPage_GetAnnot(page.Handle.DangerousGetHandle(), index);
                try
                {
                    return Fpdf.FPDFAnnot_GetFlags(annotation);
                }
                finally
                {
                    Fpdf.FPDFPage_CloseAnnot(annotation);
                }
            },
            cancellationToken: Ct);
    }
}
