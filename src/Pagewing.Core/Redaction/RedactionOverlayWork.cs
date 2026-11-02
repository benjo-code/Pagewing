using System.Collections;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;

namespace Pagewing.Core.Redaction;

/// <summary>
/// The overlay text of one page, as the redaction of that page takes it (milestone 14.1): the text of each rectangle, how it is drawn,
/// and what is left of the budget of the document. The page is laid out and decided on the PDFium thread, in the same function for the
/// analysis and for applying (<see cref="PdfiumRedactionOverlay.Decide"/>), so the card says what applying does.
/// </summary>
internal sealed class RedactionOverlayWork
{
    /// <summary>Initializes the work of a page.</summary>
    /// <param name="texts">The overlay text of each rectangle passed with it (the same order), <see langword="null"/> for none.</param>
    /// <param name="style">How the text is drawn.</param>
    /// <param name="fill">The colour of the areas (the automatic text colour reads against it).</param>
    /// <param name="budget">What is left of the budget of the document.</param>
    public RedactionOverlayWork(IReadOnlyList<string?> texts, RedactionOverlayStyle style, RedactionFill fill, OverlayBudget budget)
    {
        Texts = texts;
        Style = style;
        Color = RedactionOverlayColor.Choose(fill, style.TextColor);
        Budget = budget;
    }

    /// <summary>Gets the overlay text of each rectangle.</summary>
    public IReadOnlyList<string?> Texts { get; }

    /// <summary>Gets how the text is drawn.</summary>
    public RedactionOverlayStyle Style { get; }

    /// <summary>Gets the colour of the glyphs.</summary>
    public RedactionFill Color { get; }

    /// <summary>Gets what is left of the budget of the document.</summary>
    public OverlayBudget Budget { get; }

    /// <summary>Gets whether any rectangle has a text.</summary>
    public bool HasText => Texts.Any(static t => t is not null);

    /// <summary>The overlay text of the rectangles of a page, or <see langword="null"/> when none has one.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="page">The 0-based page.</param>
    /// <param name="areas">The rectangles the page is redacted with (a test hook may have replaced them: then the texts follow only when the count is the same).</param>
    /// <param name="budget">What is left of the budget of the document.</param>
    public static RedactionOverlayWork? For(RedactionPlan plan, int page, IReadOnlyList<PageRect> areas, OverlayBudget budget)
    {
        var texts = plan.OverlayTextsOn(page);
        if (texts.Count != areas.Count || texts.All(static t => t is null))
        {
            return null;
        }

        return new RedactionOverlayWork(texts, plan.Overlay, plan.Fill, budget);
    }

    /// <summary>Makes what the PDFium assembly needs from the work: the texts, the colour, the font and the layout, both reached only when a rectangle has a text.</summary>
    /// <param name="thread">The PDFium thread the glyphs are read on.</param>
    public PdfiumOverlaySpec ToSpec(PdfiumThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        var style = Style;
        return new PdfiumOverlaySpec
        {
            Texts = Texts,
            Color = new PdfiumRedactionFill(Color.Red, Color.Green, Color.Blue),
            Glyphs = () => TextBoxFont.Table(thread),
            Layout = (text, rect, table) =>
            {
                var placement = RedactionOverlayLayout.Fit(
                    text,
                    new PageRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                    style,
                    RedactionOverlayLayout.DefaultFontSize,
                    TextBoxFont.MetricsOf(table));
                return new PdfiumOverlayPlacement(new LineView(placement.Lines), placement.FontSize, placement.Fits);
            },
            PathBudget = Budget.Paths,
            SegmentBudget = Budget.Segments,
            PagePaths = RedactionLimits.MaxOverlayPathsPerPage,
            PageSegments = RedactionLimits.MaxOverlaySegmentsPerPage,
            LimitReached = Budget.Reached,
        };
    }

    /// <summary>The lines of a placement for the PDFium assembly, worked out on access (a repeated text has thousands of tiles; copying them would cost for nothing).</summary>
    private sealed class LineView(IReadOnlyList<OverlayLine> lines) : IReadOnlyList<PdfiumOverlayLine>
    {
        public int Count => lines.Count;

        public PdfiumOverlayLine this[int index]
        {
            get
            {
                var line = lines[index];
                return new PdfiumOverlayLine(line.Text, line.X, line.Baseline);
            }
        }

        public IEnumerator<PdfiumOverlayLine> GetEnumerator()
        {
            for (var i = 0; i < lines.Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
