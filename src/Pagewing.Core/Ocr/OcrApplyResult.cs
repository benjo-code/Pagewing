namespace Pagewing.Core.Ocr;

/// <summary>What applying a recognition run to the document wrote (see <c>AnnotationEditor.ApplyOcrAsync</c>).</summary>
/// <param name="Pages">The 0-based pages that got a layer (or a new one), in page order.</param>
/// <param name="WordsWritten">The words written.</param>
/// <param name="WordsLeftOut">The words left out because the font of the layer lacks a character of them (counted, never replaced).</param>
/// <param name="AlreadyRecognized">The pages left alone because they already had a layer and the run did not replace it.</param>
public sealed record OcrApplyResult(IReadOnlyList<int> Pages, int WordsWritten, int WordsLeftOut, IReadOnlyList<int> AlreadyRecognized);
