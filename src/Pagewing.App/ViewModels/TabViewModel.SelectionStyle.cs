using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The colour and stroke width of the selection of the select tool (milestone 10): the toolbar shows them and changes them.
/// A drawing Pagewing can redraw without loss (<see cref="CanMoveSelection"/>) gets a colour of the drawing palette and a
/// width of the three steps, each change one step of the history (the drawing is replaced, as a move replaces it); a
/// selected highlight, underline or strike-out gets a colour of the markup palette, as the "Comments" panel gives it. A
/// locked drawing of another program offers neither. The styles the drawing tools remember stay as they are.
/// </summary>
public sealed partial class TabViewModel
{
    private RelayCommand<DrawColor>? _setSelectionColorCommand;
    private RelayCommand<StrokeWidth>? _setSelectionWidthCommand;
    private RelayCommand<MarkupColor>? _setSelectionMarkupColorCommand;

    // The colour a change of the selection is on its way to (shown and compared against until it is made), so a second click
    // before the first change is made is compared with the colour the user chose, not with the old one.
    private PendingColor? _pendingColor;

    /// <summary>Gets the colour of a movable selected drawing in the drawing palette, or <see langword="null"/> (none, or a colour of another program).</summary>
    public DrawColor? SelectionDrawColor => CanStyleSelection && ShownColorOf(_selectedAnnotation!) is { } color ? DrawPalette.Match(color) : null;

    /// <summary>
    /// Gets whether the selection has a colour and a stroke width to change: a movable drawing, but not an image signature (the
    /// pixels have their colour, and no stroke).
    /// </summary>
    public bool CanStyleSelection => CanMoveSelection && _selectedAnnotation is { IsSignatureImage: false };

    /// <summary>Gets the stroke width of a movable selected drawing as one of the three steps, or <see langword="null"/> (another width).</summary>
    public StrokeWidth? SelectionStrokeWidth =>
        !CanStyleSelection ? null
        : _selectedAnnotation is { TextBox: { } box } ? TextBoxSpec.StepOf(box.FontSize)
        : SelectionGeometry is { } shape ? StepOf(shape.Width) : null;

    /// <summary>Gets whether the selection is a highlight, underline or strike-out Pagewing may recolour.</summary>
    public bool IsMarkupSelected => _selectedAnnotation is { Editable: true } selected && IsMarkupKind(selected.Kind);

    /// <summary>Gets the colour of a selected markup in the markup palette, or <see langword="null"/>.</summary>
    public MarkupColor? SelectionMarkupColor => IsMarkupSelected && ShownColorOf(_selectedAnnotation!) is { } color ? AnnotationPalette.Match(color) : null;

    /// <summary>A colour of the toolbar for the selected drawing (parameter: the <see cref="DrawColor"/>): one step of the history.</summary>
    public ICommand SetSelectionColorCommand => _setSelectionColorCommand ??= new RelayCommand<DrawColor>(color => RestyleSelection(color, null));

    /// <summary>A width of the toolbar for the selected drawing (parameter: the <see cref="StrokeWidth"/>): one step of the history.</summary>
    public ICommand SetSelectionWidthCommand => _setSelectionWidthCommand ??= new RelayCommand<StrokeWidth>(width => RestyleSelection(null, width));

    /// <summary>A colour of the toolbar for the selected markup (parameter: the <see cref="MarkupColor"/>): one step of the history.</summary>
    public ICommand SetSelectionMarkupColorCommand => _setSelectionMarkupColorCommand ??= new RelayCommand<MarkupColor>(RecolorSelectedMarkup);

    /// <summary>
    /// Selects <paramref name="annotation"/> in the document (an entry of the "Comments" panel was jumped to): only with the
    /// select tool and only what the select tool may select; otherwise nothing happens.
    /// </summary>
    internal void SelectFromList(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (!CanSelect || !DrawingHitTest.IsSelectable(annotation))
        {
            return;
        }

        // A click of the page that waits for its annotations is dropped: this choice is newer.
        _selectionRequest++;
        Select(annotation);
    }

    /// <summary>The same shape with another stroke width: a line and an arrow are made again from their ends (the head follows the width).</summary>
    internal static DrawingGeometry WithWidth(DrawingGeometry shape, double width)
    {
        ArgumentNullException.ThrowIfNull(shape);
        return shape.Kind switch
        {
            AnnotationKind.Rectangle => DrawingGeometry.Rectangle(shape.Box, width),
            AnnotationKind.Ellipse => DrawingGeometry.Ellipse(shape.Box, width),
            AnnotationKind.Signature => DrawingGeometry.Signature(shape.Strokes, width),
            AnnotationKind.Line when shape.Strokes is [[var start, var end], ..] => DrawingGeometry.Line(start, end, width),
            AnnotationKind.Arrow when shape.Strokes is [[var start, var end], ..] => DrawingGeometry.Arrow(start, end, width),
            _ => DrawingGeometry.Ink(shape.Strokes, width),
        };
    }

    private static StrokeWidth? StepOf(double points) => points switch
    {
        1 => StrokeWidth.Thin,
        2 => StrokeWidth.Medium,
        4 => StrokeWidth.Thick,
        _ => null,
    };

    /// <summary>The selection changed: its colour and width as the toolbar shows them.</summary>
    private void OnSelectionStyleChanged()
    {
        OnPropertyChanged(nameof(SelectionDrawColor));
        OnPropertyChanged(nameof(SelectionStrokeWidth));
        OnPropertyChanged(nameof(IsMarkupSelected));
        OnPropertyChanged(nameof(SelectionMarkupColor));
    }

    /// <summary>
    /// Gives the selected drawing another colour or width (one of them, or both in one step); nothing when it has them
    /// already, it cannot be redrawn without loss or the tab takes no edit now. A waiting nudge is made first.
    /// </summary>
    private void RestyleSelection(DrawColor? color, StrokeWidth? width)
    {
        if (!CanStyleSelection || !CanDraw || (color is { } c && !Enum.IsDefined(c)) || (width is { } w && !Enum.IsDefined(w)))
        {
            return;
        }

        FlushNudge();
        if (_selectedAnnotation is not { SelectionShape: { } drawing } target || !CanMoveSelection || WaitsForReplaceWithoutName(target))
        {
            return;
        }

        if (target.TextBox is { } textBox)
        {
            RestyleTextBox(target, textBox, color, width);
            return;
        }

        var shape = SelectionGeometry ?? drawing;
        AnnotationColor? newColor = color is { } chosen && DrawPalette.ColorOf(chosen) != ShownColorOf(target) ? DrawPalette.ColorOf(chosen) : null;
        DrawingGeometry? newShape = width is { } step && StrokeWidths.PointsOf(step) != shape.Width ? WithWidth(shape, StrokeWidths.PointsOf(step)) : null;
        if (newColor is null && newShape is null)
        {
            return;
        }

        var locator = new AnnotationLocator(target.Page, target.Index, target.Name);
        PendingGeometry? pending = null;
        if (newShape is not null)
        {
            pending = new PendingGeometry(target, newShape);
            _pendingGeometry = pending;
            OnPropertyChanged(nameof(SelectionGeometry));
        }

        var pendingColor = newColor is { } shownColor ? SetPendingColor(target, shownColor) : null;
        _ = RunEditAsync(async editor =>
        {
            try
            {
                var changed = await editor.UpdateAsync(locator, new AnnotationEdit(Color: newColor, Geometry: newShape));
                if (_selectedAnnotation is { } now && SameAnnotation(now, target))
                {
                    SelectedAnnotation = changed;
                }
            }
            catch (Exception ex) when (ex is not (ObjectDisposedException or OperationCanceledException or PdfAnnotationNotFoundException))
            {
                // PDFium refused: nothing changed (the editor recorded nothing); the view shows why, as for a new drawing.
                DrawingRefused?.Invoke(Strings.DrawingChangeFailed);
            }
            finally
            {
                if (pending is not null && ReferenceEquals(_pendingGeometry, pending))
                {
                    _pendingGeometry = null;
                    OnPropertyChanged(nameof(SelectionGeometry));
                    OnPropertyChanged(nameof(SelectionStrokeWidth));
                }

                ClearPendingColor(pendingColor);
            }
        });
    }

    /// <summary>
    /// Gives the selected text box another colour or font size (the width step is the size, <see cref="TextBoxSpec.FontSizeOf"/>)
    /// in one step; its text is laid out again.
    /// </summary>
    private void RestyleTextBox(PdfAnnotation target, TextBoxSpec box, DrawColor? color, StrokeWidth? width)
    {
        var newColor = color is { } chosen && DrawPalette.ColorOf(chosen) != ShownColorOf(target) ? DrawPalette.ColorOf(chosen) : (AnnotationColor?)null;
        var newSize = width is { } step && TextBoxSpec.FontSizeOf(step) != box.FontSize ? TextBoxSpec.FontSizeOf(step) : (double?)null;
        if (newColor is null && newSize is null)
        {
            return;
        }

        var locator = new AnnotationLocator(target.Page, target.Index, target.Name);
        var spec = box with { Color = newColor ?? box.Color, FontSize = newSize ?? box.FontSize };
        var pendingColor = newColor is { } shownColor ? SetPendingColor(target, shownColor) : null;
        _ = RunEditAsync(async editor =>
        {
            try
            {
                var changed = await editor.ReplaceTextBoxAsync(locator, spec);
                if (_selectedAnnotation is { } now && SameAnnotation(now, target))
                {
                    SelectedAnnotation = changed;
                }
            }
            catch (Exception ex) when (ex is not (ObjectDisposedException or OperationCanceledException or PdfAnnotationNotFoundException))
            {
                DrawingRefused?.Invoke(Strings.DrawingChangeFailed);
            }
            finally
            {
                ClearPendingColor(pendingColor);
            }
        });
    }

    /// <summary>The colour <paramref name="annotation"/> is shown with: the one a change is on its way to, else its own.</summary>
    private AnnotationColor? ShownColorOf(PdfAnnotation annotation) =>
        _pendingColor is { } pending && SameAnnotation(pending.Target, annotation) ? pending.Color : annotation.Color;

    private PendingColor SetPendingColor(PdfAnnotation target, AnnotationColor color)
    {
        var pending = new PendingColor(target, color);
        _pendingColor = pending;
        OnSelectionStyleChanged();
        return pending;
    }

    /// <summary>A change of the colour ended: it is no longer pending, unless a newer one took its place.</summary>
    private void ClearPendingColor(PendingColor? pending)
    {
        if (pending is not null && ReferenceEquals(_pendingColor, pending))
        {
            _pendingColor = null;
            OnSelectionStyleChanged();
        }
    }

    /// <summary>Gives the selected markup another colour of the markup palette (one step), as the "Comments" panel does.</summary>
    private void RecolorSelectedMarkup(MarkupColor color)
    {
        if (!IsMarkupSelected || !CanDraw || !Enum.IsDefined(color) || _selectedAnnotation is not { } target)
        {
            return;
        }

        var value = AnnotationPalette.ColorOf(color);
        if (ShownColorOf(target) == value)
        {
            return;
        }

        var locator = new AnnotationLocator(target.Page, target.Index, target.Name);
        var pending = SetPendingColor(target, value);
        _ = RunEditAsync(async editor =>
        {
            try
            {
                var changed = await editor.UpdateAsync(locator, new AnnotationEdit(Color: value));
                if (_selectedAnnotation is { } now && SameAnnotation(now, target))
                {
                    SelectedAnnotation = changed;
                }
            }
            finally
            {
                ClearPendingColor(pending);
            }
        });
    }

    /// <summary>A colour a change of <paramref name="Target"/> is on its way to.</summary>
    private sealed record PendingColor(PdfAnnotation Target, AnnotationColor Color);
}
