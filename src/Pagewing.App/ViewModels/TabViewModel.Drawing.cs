using System.Windows.Input;
using Pagewing.Core.Annotations;

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
    private bool _isSelectTool;

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
                // The select tool takes the place of the tool that is on.
                ActiveTool = null;
            }

            if (SetProperty(ref _isSelectTool, value))
            {
                OnActiveToolChangedForDrawing();
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

    /// <summary>Whether the kind is made by a drawing tool: freehand, rectangle, oval, line or arrow (the text box has its own tool later).</summary>
    internal static bool IsDrawKind(AnnotationKind kind) =>
        kind is AnnotationKind.Ink or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Line or AnnotationKind.Arrow;

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
    }
}
