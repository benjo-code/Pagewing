using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The drawing tools of the mode "Comment" (milestone 10): freehand, rectangle, oval, line and arrow, the select tool that
/// moves and resizes drawings, and the style (colour and stroke width) each drawing tool remembers in the settings. This
/// part only holds the state and the commands; the drawing itself is done by the document view. Like the text markup
/// tools the drawing tools exist in the mode only, and a tool stays on after a drawing until another is chosen or the mode
/// ends. The select tool is a tool of the mode too and excludes the others: with it no drawing tool is on, and the other
/// way round.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>The tolerance in points with which a freehand stroke is thinned before it is smoothed (see DECISIONS).</summary>
    private const double SimplifyTolerance = 0.5;

    private bool _isSelectTool;

    // The last drawing queued: the next one is made after it (QueueDrawing).
    private Task _drawingTail = Task.CompletedTask;
    private int _drawingsInFlight;

    private RelayCommand? _selectToolCommand;
    private RelayCommand? _chooseSelectToolCommand;
    private RelayCommand<ToolStyle>? _setStyleCommand;
    private RelayCommand? _inkCommand;
    private RelayCommand? _rectangleCommand;
    private RelayCommand? _ellipseCommand;
    private RelayCommand? _lineCommand;
    private RelayCommand? _arrowCommand;

    /// <summary>
    /// Gets whether the select tool is on: a click selects a drawing, a drag moves it. It is on only in the mode, and with
    /// it <see cref="ActiveTool"/> is <see langword="null"/>.
    /// </summary>
    public bool IsSelectTool
    {
        get => _isSelectTool;
        private set
        {
            if (value)
            {
                // The select tool takes the place of the tool that is on (and of a signature that waits to be placed).
                DiscardPendingSignature();
                ActiveTool = null;
            }

            if (SetProperty(ref _isSelectTool, value))
            {
                if (!value)
                {
                    // The selection belongs to the select tool (TabViewModel.Selection.cs): a waiting nudge is made first.
                    ClearSelection();
                }

                OnActiveToolChangedForDrawing();
                RaiseSelectionCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Gets the style of the drawing tool that is on (without one, of the last drawing tool chosen): the colour and stroke
    /// width remembered for that kind in the settings (red and medium at first).
    /// </summary>
    public ToolStyle ActiveStyle => StyleOf(_activeTool is { } tool && IsDrawKind(tool) ? tool : _lastDrawTool);

    /// <summary>The select tool in the toolbar: turns it on (the drawing tool goes off) or, when it is on, off. Only in the mode.</summary>
    public ICommand SelectToolCommand => _selectToolCommand ??= new RelayCommand(
        () => IsSelectTool = !_isSelectTool,
        () => _isAnnotating);

    /// <summary>
    /// Chooses the style of the drawing tool that is on (parameter: the <see cref="ToolStyle"/>) and the settings remember it
    /// for that tool; the styles of the other tools stay. A style the tool has already changes and writes nothing. Not
    /// executable without a drawing tool (the text markup tools have their colour, <see cref="SetColorCommand"/>).
    /// </summary>
    public ICommand SetStyleCommand => _setStyleCommand ??= new RelayCommand<ToolStyle>(
        style =>
        {
            if (_activeTool is not { } kind || !IsDrawKind(kind) || AnnotationSettings.Current.StyleOf(kind) == style)
            {
                return;
            }

            // A new dictionary for every change: the settings never edit the one the earlier settings hold.
            AnnotationSettings.Update(current => current.WithToolStyle(kind, style));
        },
        style => _isAnnotating && _activeTool is { } kind && IsDrawKind(kind) && Enum.IsDefined(style.Color) && Enum.IsDefined(style.Width));

    /// <summary>Ctrl+Shift+M: starts the mode with the select tool, or turns it on in the mode (unlike <see cref="SelectToolCommand"/> it never turns it off).</summary>
    public ICommand ChooseSelectToolCommand => _chooseSelectToolCommand ??= new RelayCommand(
        () =>
        {
            if (_isAnnotating && CanRunMarkup())
            {
                IsSelectTool = true;
                return;
            }

            _ = RequestAnnotateAsync(null, apply: false, selectTool: true);
        },
        CanStartTool);

    /// <summary>Ctrl+Shift+I: starts the mode with the freehand tool, or turns it on in the mode.</summary>
    public ICommand InkCommand => _inkCommand ??= DrawCommand(AnnotationKind.Ink);

    /// <summary>Ctrl+Shift+R: as <see cref="InkCommand"/>, with the rectangle.</summary>
    public ICommand RectangleCommand => _rectangleCommand ??= DrawCommand(AnnotationKind.Rectangle);

    /// <summary>Ctrl+Shift+E: as <see cref="InkCommand"/>, with the oval.</summary>
    public ICommand EllipseCommand => _ellipseCommand ??= DrawCommand(AnnotationKind.Ellipse);

    /// <summary>Ctrl+Shift+L: as <see cref="InkCommand"/>, with the line.</summary>
    public ICommand LineCommand => _lineCommand ??= DrawCommand(AnnotationKind.Line);

    /// <summary>Ctrl+Shift+A: as <see cref="InkCommand"/>, with the arrow.</summary>
    public ICommand ArrowCommand => _arrowCommand ??= DrawCommand(AnnotationKind.Arrow);

    /// <summary>Gets the style a drawing tool has now: the one the settings remember for it, or the default (red, medium).</summary>
    /// <param name="kind">The kind of annotation the tool creates.</param>
    /// <returns>The style.</returns>
    public ToolStyle StyleOf(AnnotationKind kind) => AnnotationSettings.Current.StyleOf(kind);

    /// <summary>
    /// Gets or sets how the document view drops the stroke of a drawing tool that is being dragged (set by the view from the
    /// press to the end of the stroke, else <see langword="null"/>): the first meaning of Esc in the mode
    /// (<see cref="EscapeAnnotate"/>). Returns whether there was one.
    /// </summary>
    internal Func<bool>? CancelDrawingGesture { get; set; }

    /// <summary>
    /// Gets whether a press of a drawing tool may start a stroke now: the mode is on with an open document whose first sharp
    /// page was shown, no card asks about the tab, editing is not locked for an update, and the tab neither saves nor prints.
    /// </summary>
    internal bool CanDraw => CanRunEdit && !_isSaving && !_isPrinting;

    /// <summary>Gets the stroke width in points of a drawing tool (its style): the only place a drawing gets its width from.</summary>
    /// <param name="kind">The kind the tool draws.</param>
    /// <returns>1, 2 or 4 points.</returns>
    internal double StrokeWidthOf(AnnotationKind kind) => StrokeWidths.PointsOf(StyleOf(kind).Width);

    /// <summary>Gets the colour of a drawing tool (its style).</summary>
    /// <param name="kind">The kind the tool draws.</param>
    /// <returns>The RGB value.</returns>
    internal AnnotationColor DrawColorOf(AnnotationKind kind) => DrawPalette.ColorOf(StyleOf(kind).Color);

    /// <summary>
    /// Makes the drawing of a stroke of a drawing tool: one annotation on <paramref name="page"/>, in the colour of the tool of
    /// its kind and by the author of the settings, as a markup is made (<see cref="ApplySelectionAsync"/>). The geometry carries
    /// its width (<see cref="StrokeWidthOf"/>). An empty geometry makes nothing and leaves the history alone. Runs in the
    /// background through the editor; the UI never waits for it. From the call on the tab counts as having changes
    /// (<see cref="AsksBeforeClosing"/>). A drawing the document has no room for (<see cref="PdfAnnotationLimitException"/>) or
    /// that PDFium refuses changes nothing; <see cref="DrawingRefused"/> says why.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="geometry">The shape in page points of the page as shown.</param>
    /// <returns>Completes when the drawing was made or refused; it never fails.</returns>
    internal Task ApplyDrawingAsync(int page, DrawingGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (_disposed || !IsDrawKind(geometry.Kind) || geometry.Kind == AnnotationKind.TextBox || geometry.IsEmpty || !CanDraw)
        {
            return Task.CompletedTask;
        }

        return QueueDrawing(page, geometry.Kind, Task.FromResult(geometry));
    }

    /// <summary>
    /// Makes the drawing of a freehand stroke from the raw points of the gesture: the points are thinned and smoothed
    /// (<see cref="ReduceFreehand"/>) on a thread-pool thread, never on the UI thread (the thinning is quadratic in the worst
    /// case: 20,000 points of a zig-zag took 1.4 s), and then the drawing is made as <see cref="ApplyDrawingAsync"/> makes it,
    /// with the stroke width of the style of the freehand tool (<see cref="StrokeWidthOf"/>: the only place it comes from). A stroke
    /// without extent makes nothing and leaves the history alone. Strokes and shapes are made in the order they were
    /// released, whatever the thinning of each takes. From the call on the tab counts as having changes; a stroke whose
    /// document was replaced by another file, or whose tab was disposed, before the thinning ended is dropped without a trace.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="rawPoints">The points in page points of the page as shown. The tab reads the list on another thread: the caller must not change it afterwards.</param>
    /// <returns>Completes when the drawing was made, refused or dropped; it never fails.</returns>
    internal Task ApplyFreehandAsync(int page, IReadOnlyList<DisplayPoint> rawPoints)
    {
        ArgumentNullException.ThrowIfNull(rawPoints);
        if (_disposed || rawPoints.Count < 2 || !CanDraw)
        {
            return Task.CompletedTask;
        }

        // Read here, on the UI thread: the style and the reducer as they are at the release.
        var width = StrokeWidthOf(AnnotationKind.Ink);
        var reduce = FreehandReducer ?? ReduceFreehand;
        return QueueDrawing(page, AnnotationKind.Ink, Task.Run(() => DrawingGeometry.Ink([reduce(rawPoints)], width)));
    }

    /// <summary>
    /// Thins a freehand stroke (Douglas-Peucker, <see cref="SimplifyTolerance"/>) and rounds its corners. Pure computation: no
    /// PDFium, no UI.
    /// </summary>
    /// <param name="points">The raw points of the stroke.</param>
    /// <returns>The points the stroke is written with.</returns>
    internal static IReadOnlyList<DisplayPoint> ReduceFreehand(IReadOnlyList<DisplayPoint> points) =>
        StrokeSimplifier.Smooth(StrokeSimplifier.Simplify(points, SimplifyTolerance));

    /// <summary>Gets or sets what thins a freehand stroke instead of <see cref="ReduceFreehand"/> (tests hold it to order and cancel the strokes); it runs on a thread-pool thread.</summary>
    internal Func<IReadOnlyList<DisplayPoint>, IReadOnlyList<DisplayPoint>>? FreehandReducer { get; set; }

    /// <summary>
    /// Raised on the UI thread when a drawing was not made, with the text that says why (the document view shows it as a
    /// hint): the document lists as many annotations as Pagewing reads, or PDFium refused it.
    /// </summary>
    internal event Action<string>? DrawingRefused;

    /// <summary>
    /// Takes a drawing in: the colour and the author are read now, the tab counts as having changes from here on, and the
    /// drawing is made after every drawing queued before it, so the order of the releases is the order of the history
    /// whatever the geometry of each takes to be ready.
    /// </summary>
    private Task QueueDrawing(int page, AnnotationKind kind, Task<DrawingGeometry> geometry, AnnotationColor? inkColor = null)
    {
        var color = inkColor ?? DrawColorOf(kind);
        var author = AnnotationSettings.Current.AuthorName;
        var run = Track(ApplyDrawingCoreAsync(_documentGeneration, page, geometry, _drawingTail, color, author));
        _drawingTail = run;
        return run;
    }

    private async Task ApplyDrawingCoreAsync(int generation, int page, Task<DrawingGeometry> pending, Task before, AnnotationColor color, string? author)
    {
        try
        {
            // Counted before the first await, down again in the finally: the task ends after the count is down (Track).
            _pendingEdits++;
            _drawingsInFlight++;
            RaiseStepCanExecuteChanged();
            DrawingGeometry? geometry = null;
            var reductionFailed = false;
            try
            {
                geometry = await pending;
            }
            catch (Exception)
            {
                // The thinning failed (it is pure computation, so this is not expected); nothing is made. This run still waits
                // for the drawings queued before it below, so the ones after it never overtake them and the hint comes in order.
                reductionFailed = true;
            }

            try
            {
                // Every drawing queued earlier is done first (their tasks never fail; only their end matters).
                await before;
            }
            catch (Exception)
            {
                // Nothing to learn from it here.
            }

            if (reductionFailed)
            {
                DrawingRefused?.Invoke(Strings.DrawingFailed);
                return;
            }

            if (geometry is null || _disposed || generation != _documentGeneration || geometry.IsEmpty)
            {
                // Another file is open in the tab, or the tab is gone, or there is nothing to draw: nothing is made.
                return;
            }

            PdfAnnotation? created;
            try
            {
                created = await Editor.ApplyDrawingAsync(page, geometry, color, null, author);
            }
            catch (PdfAnnotationLimitException)
            {
                DrawingRefused?.Invoke(string.Format(Strings.Culture, CompositeFormat.Parse(Strings.DrawingLimitReached), PdfDocument.MaxAnnotations));
                return;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                // The document or the tab went meanwhile: nothing to say about it.
                return;
            }
            catch (Exception)
            {
                // PDFium refused the drawing: nothing changed (the editor recorded nothing). The task never fails.
                DrawingRefused?.Invoke(Strings.DrawingFailed);
                return;
            }

            if (created is not null)
            {
                _comments?.SelectWhenListed(created);
                if (created.Kind == AnnotationKind.Signature)
                {
                    SelectPlaced(created);
                }
            }
        }
        finally
        {
            _pendingEdits--;
            _drawingsInFlight--;
            RaiseStepCanExecuteChanged();
            SyncFromEditor();
        }
    }

    /// <summary>
    /// Gets the last drawing queued (<see cref="QueueDrawing"/>): the task ends after the drawing was made, refused or dropped
    /// and never fails. Undo and redo wait for it, so a Ctrl+Z right after the release undoes that stroke.
    /// </summary>
    private Task DrawingTail => _drawingTail;

    /// <summary>Undo and redo are executable while a drawing is in flight: it is the change they act on once it is made.</summary>
    private bool HasDrawingInFlight => _drawingsInFlight > 0;

    private void RaiseStepCanExecuteChanged()
    {
        _undoCommand?.RaiseCanExecuteChanged();
        _redoCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Whether the kind is a drawing with a colour and a stroke width: freehand, rectangle, oval, line and arrow (each made by a tool
    /// with a style), the text box (whose width step is its font size, <see cref="TextBoxSpec.FontSizeOf"/>; see
    /// <c>TabViewModel.TextBoxes.cs</c>) and the drawn signature (placed from the library, so no tool of the toolbar, see
    /// <c>TabViewModel.Signature.cs</c>).
    /// </summary>
    internal static bool IsDrawKind(AnnotationKind kind) =>
        kind is AnnotationKind.Ink or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.TextBox or AnnotationKind.Signature;

    private RelayCommand DrawCommand(AnnotationKind kind) => new(
        () =>
        {
            if (_isAnnotating && CanRunMarkup())
            {
                ActiveTool = kind;
                return;
            }

            _ = RequestAnnotateAsync(kind, apply: false);
        },
        CanStartTool);

    /// <summary>Whether a tool command can run: the mode is on and a change is possible, or the mode can start.</summary>
    private bool CanStartTool() => (_isAnnotating && CanRunMarkup()) || CanBeginAnnotate();

    /// <summary>The tool (or the select tool) changed: the style shown is the one of the new tool, and the style command may run or not.</summary>
    private void OnActiveToolChangedForDrawing()
    {
        OnPropertyChanged(nameof(ActiveStyle));
        _setStyleCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>The mode started, ended or can start or not: the commands of the tools ask again.</summary>
    private void RaiseDrawCanExecuteChanged()
    {
        _selectToolCommand?.RaiseCanExecuteChanged();
        _chooseSelectToolCommand?.RaiseCanExecuteChanged();
        _setToolCommand?.RaiseCanExecuteChanged();
        _setStyleCommand?.RaiseCanExecuteChanged();
        _inkCommand?.RaiseCanExecuteChanged();
        _rectangleCommand?.RaiseCanExecuteChanged();
        _ellipseCommand?.RaiseCanExecuteChanged();
        _lineCommand?.RaiseCanExecuteChanged();
        _arrowCommand?.RaiseCanExecuteChanged();
        _textBoxCommand?.RaiseCanExecuteChanged();
        RaiseSelectionCanExecuteChanged();
    }
}
