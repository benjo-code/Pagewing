using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Pagewing.Core.TextEdit;

namespace Pagewing.App.Controls;

/// <summary>
/// The layer of the mode "Edit text" (milestone 16) over the pages: it outlines the run under the pointer and the run with the keyboard focus, and marks a
/// locked run with a lock (the outline is dashed as well, so the state is never told by colour alone). It takes no input; the document view finds the run
/// for a point and tells the layer what to draw (<see cref="Show"/>). The reason a run is locked is the tooltip of the view and the text of the hint bar.
/// </summary>
internal sealed class TextEditOverlay : Control
{
    private const double BadgeSize = 16;
    private const double BadgeIconSize = 12;
    private const double Outset = 2;

    // Tabler outline "lock" (24 x 24, stroke 2), parsed when the layer is first made.
    private static readonly Geometry LockIcon = StreamGeometry.Parse(
        "M5 13a2 2 0 0 1 2 -2h10a2 2 0 0 1 2 2v6a2 2 0 0 1 -2 2h-10a2 2 0 0 1 -2 -2v-6 M11 16a1 1 0 1 0 2 0a1 1 0 0 0 -2 0 M8 11v-4a4 4 0 1 1 8 0v4");

    private readonly DocumentCanvas _canvas;
    private TextRun? _hover;
    private TextRun? _focus;

    public TextEditOverlay(DocumentCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _canvas = canvas;
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Gets the run under the pointer, as last given.</summary>
    internal TextRun? Hover => _hover;

    /// <summary>Gets the run with the keyboard focus, as last given.</summary>
    internal TextRun? FocusedRun => _focus;

    /// <summary>Tells the layer which runs to outline.</summary>
    /// <param name="hover">The run under the pointer, or <see langword="null"/>.</param>
    /// <param name="focus">The run with the keyboard focus, or <see langword="null"/>.</param>
    internal void Show(TextRun? hover, TextRun? focus)
    {
        _hover = hover;
        _focus = focus;
        InvalidateVisual();
    }

    /// <summary>Draws again (the view scrolled or zoomed, or the theme changed).</summary>
    internal void Refresh() => InvalidateVisual();

    /// <summary>Gets where a run is in the viewport (DIPs), or <see langword="null"/> when its page is not laid out.</summary>
    internal Rect? ViewRectOf(TextRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_canvas.PagePlacement(run.Page) is not { } placement)
        {
            return null;
        }

        var (page, scale) = placement;
        return new Rect(page.X + (run.Bounds.Left * scale), page.Y + (run.Bounds.Top * scale), run.Bounds.Width * scale, run.Bounds.Height * scale).Inflate(Outset);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var accent = Resolve("Pw.Brush.Accent.Default", Colors.RoyalBlue);
        var neutral = Resolve("Pw.Brush.Text.Secondary", Colors.Gray);
        var surface = Resolve("Pw.Brush.Surface.Raised", Colors.White);
        if (_hover is { } hover && !Equals(hover, _focus))
        {
            Draw(context, hover, accent, neutral, surface, 2);
        }

        if (_focus is { } focus)
        {
            Draw(context, focus, accent, neutral, surface, 3);
        }
    }

    private void Draw(DrawingContext context, TextRun run, IBrush accent, IBrush neutral, IBrush surface, double thickness)
    {
        if (ViewRectOf(run) is not { } rect)
        {
            return;
        }

        if (run.IsLocked)
        {
            var dashed = new Pen(neutral, thickness, new DashStyle([3, 2], 0));
            context.DrawRectangle(null, dashed, rect, 3, 3);
            var badge = new Rect(rect.Right - (BadgeSize / 2), rect.Top - (BadgeSize / 2), BadgeSize, BadgeSize);
            context.DrawRectangle(surface, new Pen(neutral, 1), badge, 3, 3);
            var inset = (BadgeSize - BadgeIconSize) / 2;
            using var transform = context.PushTransform(Matrix.CreateScale(BadgeIconSize / 24, BadgeIconSize / 24) * Matrix.CreateTranslation(badge.X + inset, badge.Y + inset));
            context.DrawGeometry(null, new Pen(neutral, 1.5), LockIcon);
            return;
        }

        context.DrawRectangle(null, new Pen(accent, thickness), rect, 3, 3);
    }

    private IBrush Resolve(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : new ImmutableSolidColorBrush(fallback);
}
