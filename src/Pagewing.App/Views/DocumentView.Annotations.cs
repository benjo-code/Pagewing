using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.App.Views;

/// <summary>
/// Marking with the mouse in the mode "Comment" (milestone 9, design system 7.7): with a tool of the mode, releasing the
/// left button after a text selection makes the markup at once (<see cref="TabViewModel.ApplySelectionAsync"/>, one
/// annotation per page) and clears the selection. A drag, a double click (a word) and a triple click (a line) mark; a click
/// without a drag (under <see cref="ClickSlop"/>) marks nothing. A double click waits for the time of a further click
/// before it marks, because a third click turns the word into its line and only that should become a markup. Without a
/// tool the selection only selects (Ctrl+C copies it). Nothing of this before the first sharp page: there is no text
/// before it (<see cref="TabViewModel.PageTexts"/>).
/// </summary>
public sealed partial class DocumentView
{
    // The word of a double click that waits for a third click before it is marked; and its timer.
    private (TabViewModel Tab, TextSelection Selection, AnnotationKind Tool)? _pendingMarkup;
    private DispatcherTimer? _pendingMarkupTimer;
    private TimeSpan? _multiClickMarkupDelay;

    /// <summary>
    /// Gets or sets how long the word of a double click waits for a third click before it becomes a markup: the double
    /// click time of the platform (500 ms without one). Tests shorten it.
    /// </summary>
    internal TimeSpan MultiClickMarkupDelay
    {
        get => _multiClickMarkupDelay ??= Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) is { } time && time > TimeSpan.Zero
            ? time
            : TimeSpan.FromMilliseconds(500);
        set => _multiClickMarkupDelay = value;
    }

    /// <summary>Gets whether the word of a double click waits to be marked (for tests).</summary>
    internal bool IsMarkupPending => _pendingMarkup is not null;

    /// <summary>The tool a release marks with: the mode "Comment" is on with a text markup tool; otherwise <see langword="null"/> (a drawing tool does not mark text).</summary>
    private static AnnotationKind? MarksOnRelease(TabViewModel tab) =>
        tab is { IsAnnotating: true, ActiveTool: { } tool, HasShownSharpPage: true } && TabViewModel.IsMarkupKind(tool) ? tool : null;

    /// <summary>
    /// The release of a press: with a tool of the mode, a selection made by a drag, a double or a triple click becomes a
    /// markup. A click without a drag marks nothing; a fourth click and more mark nothing either (the line was marked).
    /// </summary>
    private void ApplyMarkupOnRelease(TextPress press, Point position)
    {
        if (_viewModel is not { } tab || MarksOnRelease(tab) is not { } tool
            || press.Kind is not (PressKind.Selecting or PressKind.Done) || tab.TextSelection is not { IsEmpty: false } selection)
        {
            return;
        }

        switch (press.ClickCount)
        {
            case <= 1 when !press.Moved && Distance(position, press.Start) < ClickSlop:
                return;
            case <= 1:
                Mark(tab, selection, tool);
                return;
            case 2:
                // A third click may follow and make it a line: wait for it.
                WaitForThirdClick(tab, selection, tool);
                return;
            case 3:
                Mark(tab, selection, tool);
                return;
        }
    }

    /// <summary>Makes the markup and clears the selection; the markup appears when its page is rendered again.</summary>
    private static void Mark(TabViewModel tab, TextSelection selection, AnnotationKind tool)
    {
        tab.TextSelection = null;
        _ = tab.ApplySelectionAsync(selection, tool);
    }

    private void WaitForThirdClick(TabViewModel tab, TextSelection selection, AnnotationKind tool)
    {
        _pendingMarkup = (tab, selection, tool);
        if (_pendingMarkupTimer is null)
        {
            _pendingMarkupTimer = new DispatcherTimer();
            _pendingMarkupTimer.Tick += (_, _) => FlushPendingMarkup();
        }

        _pendingMarkupTimer.Stop();
        _pendingMarkupTimer.Interval = MultiClickMarkupDelay;
        _pendingMarkupTimer.Start();
    }

    /// <summary>
    /// A press arrives: a third click of a double click drops the waiting word (its line follows on the release); any
    /// other press marks the word now, before the press clears the selection.
    /// </summary>
    private void OnPressForMarkup(int clickCount)
    {
        if (_pendingMarkup is null)
        {
            return;
        }

        if (clickCount >= 3)
        {
            CancelPendingMarkup();
        }
        else
        {
            FlushPendingMarkup();
        }
    }

    /// <summary>Marks the waiting word, unless the selection or the mode changed meanwhile.</summary>
    private void FlushPendingMarkup()
    {
        var pending = _pendingMarkup;
        CancelPendingMarkup();
        if (pending is not { } markup || !ReferenceEquals(markup.Tab, _viewModel) || MarksOnRelease(markup.Tab) is null
            || markup.Tab.TextSelection != markup.Selection)
        {
            return;
        }

        Mark(markup.Tab, markup.Selection, markup.Tool);
    }

    private void CancelPendingMarkup()
    {
        _pendingMarkupTimer?.Stop();
        _pendingMarkup = null;
    }
}
