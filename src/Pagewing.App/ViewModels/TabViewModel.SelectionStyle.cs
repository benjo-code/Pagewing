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

    /// <summary>Gets the colour of a movable selected drawing in the drawing palette, or <see langword="null"/> (none, or a colour of another program).</summary>
    public DrawColor? SelectionDrawColor => CanMoveSelection && _selectedAnnotation!.Color is { } color ? DrawPalette.Match(color) : null;

    /// <summary>Gets the stroke width of a movable selected drawing as one of the three steps, or <see langword="null"/> (another width).</summary>
    public StrokeWidth? SelectionStrokeWidth => CanMoveSelection && SelectionGeometry is { } shape ? StepOf(shape.Width) : null;

    /// <summary>Gets whether the selection is a highlight, underline or strike-out Pagewing may recolour.</summary>
    public bool IsMarkupSelected => _selectedAnnotation is { Editable: true } selected && IsMarkupKind(selected.Kind);

    /// <summary>Gets the colour of a selected markup in the markup palette, or <see langword="null"/>.</summary>
    public MarkupColor? SelectionMarkupColor => IsMarkupSelected && _selectedAnnotation!.Color is { } color ? AnnotationPalette.Match(color) : null;

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
        if (!CanMoveSelection || !CanDraw || (color is { } c && !Enum.IsDefined(c)) || (width is { } w && !Enum.IsDefined(w)))
        {
            return;
        }

        FlushNudge();
        if (_selectedAnnotation is not { Drawing: { } drawing } target || !CanMoveSelection || WaitsForReplaceWithoutName(target))
        {
            return;
        }

        var shape = SelectionGeometry ?? drawing;
        AnnotationColor? newColor = color is { } chosen && DrawPalette.ColorOf(chosen) != target.Color ? DrawPalette.ColorOf(chosen) : null;
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
                }
            }
        });
    }

    /// <summary>Gives the selected markup another colour of the markup palette (one step), as the "Comments" panel does.</summary>
    private void RecolorSelectedMarkup(MarkupColor color)
    {
        if (!IsMarkupSelected || !CanDraw || !Enum.IsDefined(color) || _selectedAnnotation is not { } target)
        {
            return;
        }

        var value = AnnotationPalette.ColorOf(color);
        if (target.Color == value)
        {
            return;
        }

        var locator = new AnnotationLocator(target.Page, target.Index, target.Name);
        _ = RunEditAsync(async editor =>
        {
            var changed = await editor.UpdateAsync(locator, new AnnotationEdit(Color: value));
            if (_selectedAnnotation is { } now && SameAnnotation(now, target))
            {
                SelectedAnnotation = changed;
            }
        });
    }
}
