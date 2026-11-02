using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Redaction;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>Which hand the mode "Redact" lends the pointer: it drags out areas, or it selects text on the page (milestone 14).</summary>
public enum RedactionTool
{
    /// <summary>A drag on a page marks the rectangle it spans.</summary>
    Area,

    /// <summary>A drag selects text as in the reading view; "Mark selection" turns the selection into a mark.</summary>
    Text,
}

/// <summary>
/// Marking on the page for the mode "Redact" (milestone 14, design spec 5.6): the tool the pointer has, the marks that a drag, a
/// handle, the keys and a text selection make or change, and the notices that tell why nothing started. The plan, its history and
/// the selection of marks are in <c>TabViewModel.Redaction.cs</c>; the drawing and the pointer are in the document view
/// (<c>DocumentView.Redaction.cs</c>, <c>Controls/RedactionOverlay</c>).
/// </summary>
/// <remarks>
/// Only a mark of the kind <see cref="RedactionKind.Area"/> moves and changes its size: a mark of a text selection or a hit has
/// its term, and its rectangles are the places of exactly that text, so changing them would let the check look for a term where
/// the mark no longer is. Such a mark is deleted and made again. One drag of a mark is one step of the history, and so is a run
/// of arrow keys (<see cref="NudgeDelay"/> between two presses at most): a press is merged into the step of the one before.
/// </remarks>
public sealed partial class TabViewModel
{
    private RedactionTool _redactionTool;
    private RelayCommand? _markSelectionCommand;
    private RelayCommand? _redactEntryCommand;
    private RelayCommand<RedactionTool>? _chooseRedactionToolCommand;
    private RelayCommand<RedactionFill>? _setRedactionFillCommand;
    private RelayCommand<RedactionImageTreatment>? _setRedactionImageTreatmentCommand;
    private RelayCommand<RedactionImageStrength>? _setRedactionImageStrengthCommand;

    // The run of arrow keys that is one step: the version of the history right after its last press, when that was, and which marks.
    private int _markNudgeVersion = -1;
    private long _markNudgeStamp;
    private HashSet<Guid>? _markNudgeMarks;

    /// <summary>
    /// Gets or sets what a drag on a page does in the mode: marks an area (the default when the mode starts) or selects text.
    /// Setting it outside the mode does nothing.
    /// </summary>
    public RedactionTool RedactionTool
    {
        get => _redactionTool;
        set
        {
            if (_isRedacting && Enum.IsDefined(value) && SetProperty(ref _redactionTool, value))
            {
                OnPropertyChanged(nameof(RedactionToolIsArea));
                OnPropertyChanged(nameof(RedactionToolIsText));
            }
        }
    }

    /// <summary>Gets whether the tool is the area (a state of the toolbar button: a frame and an accent label, not colour alone).</summary>
    public bool RedactionToolIsArea => _isRedacting && _redactionTool == RedactionTool.Area;

    /// <summary>Gets whether the tool is the text selection.</summary>
    public bool RedactionToolIsText => _isRedacting && _redactionTool == RedactionTool.Text;

    /// <summary>Gets whether the areas are filled black (the default), not white.</summary>
    public bool IsRedactionFillBlack => RedactionFill == RedactionFill.Black;

    /// <summary>Gets whether the areas are filled white.</summary>
    public bool IsRedactionFillWhite => RedactionFill == RedactionFill.White;

    /// <summary>Gets whether the areas are filled with a colour of the colour field, neither black nor white.</summary>
    public bool IsRedactionFillCustom => !IsRedactionFillBlack && !IsRedactionFillWhite;

    /// <summary>Gets the fill written as <c>#RRGGBB</c> (the text of the colour field).</summary>
    public string RedactionFillHex => RedactionFill.ToHex();

    /// <summary>
    /// Gets or sets what the parts of images under the marks become (solid fill at first; pixelating and blurring leave a few coarse
    /// blocks). Setting it in the mode is a step of the history, like the fill; outside the mode, with a card over the tab or with a value
    /// that is none of the treatments it does nothing.
    /// </summary>
    public RedactionImageTreatment RedactionImageTreatment
    {
        get => _redactionHistory?.Current.ImageTreatment ?? RedactionImageTreatment.Solid;
        set
        {
            if (Enum.IsDefined(value))
            {
                StageRedaction(plan => plan.WithImageTreatment(value), null);
            }
        }
    }

    /// <summary>Gets or sets how coarse pixelating and blurring are (the same rules as <see cref="RedactionImageTreatment"/>).</summary>
    public RedactionImageStrength RedactionImageStrength
    {
        get => _redactionHistory?.Current.ImageStrength ?? RedactionImageStrength.Coarse;
        set
        {
            if (Enum.IsDefined(value))
            {
                StageRedaction(plan => plan.WithImageStrength(value), null);
            }
        }
    }

    /// <summary>Gets whether the parts of images get the solid fill (the default).</summary>
    public bool IsRedactionImageSolid => RedactionImageTreatment == RedactionImageTreatment.Solid;

    /// <summary>Gets whether the parts of images are pixelated.</summary>
    public bool IsRedactionImagePixelate => RedactionImageTreatment == RedactionImageTreatment.Pixelate;

    /// <summary>Gets whether the parts of images are blurred.</summary>
    public bool IsRedactionImageBlur => RedactionImageTreatment == RedactionImageTreatment.Blur;

    /// <summary>Gets whether pixelating or blurring is chosen, so that the strength means something.</summary>
    public bool IsRedactionImageTreated => !IsRedactionImageSolid;

    /// <summary>Gets whether the blocks are the coarse ones (up to 6 along an axis).</summary>
    public bool IsRedactionImageCoarse => RedactionImageStrength == RedactionImageStrength.Coarse;

    /// <summary>Gets whether the blocks are the very coarse ones (up to 3 along an axis).</summary>
    public bool IsRedactionImageVeryCoarse => RedactionImageStrength == RedactionImageStrength.VeryCoarse;

    /// <summary>Gets the chosen treatment in words, in the language of the interface (the value next to the name of the toolbar button, so the state is never told by colour alone).</summary>
    public string RedactionImageTreatmentText => RedactionImageTreatment switch
    {
        RedactionImageTreatment.Pixelate => Strings.RedactImagePixelate,
        RedactionImageTreatment.Blur => Strings.RedactImageBlur,
        _ => Strings.RedactImageSolid,
    };

    /// <summary>
    /// Gets the note under the preview: that it changes nothing in the file and, with pixelating or blurring chosen, that a dashed frame
    /// marks the areas and the preview never shows the pixels of the image, and, when a mark has an overlay text, that the preview shows the fill only.
    /// </summary>
    public string RedactionPreviewNote
    {
        get
        {
            var note = IsRedactionImageTreated ? Strings.RedactPreviewNote + " " + Strings.RedactPreviewImageNote : Strings.RedactPreviewNote;
            return RedactionPlan?.Marks.Any(static m => m.OverlayText is not null) == true ? note + " " + Strings.RedactPreviewOverlayNote : note;
        }
    }

    /// <summary>Gets the counter of the marks, "4 marks on 2 pages" (the text of the toolbar and its name for screen readers).</summary>
    public string RedactionCounterText
    {
        get
        {
            var marks = Strings.RedactCount(RedactionPlan?.Marks.Count ?? 0, RedactionPlan?.Pages.Count ?? 0);
            var raster = RedactionPlan?.RasterPages.Count ?? 0;
            return raster == 0 ? marks : (RedactionPlan?.Marks.Count ?? 0) == 0 ? Strings.RedactRasterCount(raster) : marks + ", " + Strings.RedactRasterCount(raster);
        }
    }

    /// <summary>Chooses the tool of the pointer (parameter: a <see cref="RedactionTool"/>).</summary>
    public ICommand ChooseRedactionToolCommand => _chooseRedactionToolCommand ??= new RelayCommand<RedactionTool>(
        tool => RedactionTool = tool,
        _ => _isRedacting && !_isAskedAbout);

    /// <summary>Chooses the colour of the areas (parameter: a <see cref="RedactionFill"/>); a step of the history.</summary>
    public ICommand SetRedactionFillCommand => _setRedactionFillCommand ??= new RelayCommand<RedactionFill>(
        fill => RedactionFill = fill,
        _ => CanEditRedactions);

    /// <summary>Chooses what the parts of images under the marks become (parameter: a <see cref="RedactionImageTreatment"/>); a step of the history.</summary>
    public ICommand SetRedactionImageTreatmentCommand => _setRedactionImageTreatmentCommand ??= new RelayCommand<RedactionImageTreatment>(
        treatment => RedactionImageTreatment = treatment,
        _ => CanEditRedactions);

    /// <summary>Chooses how coarse pixelating and blurring are (parameter: a <see cref="RedactionImageStrength"/>); a step of the history, possible while a treatment with blocks is chosen.</summary>
    public ICommand SetRedactionImageStrengthCommand => _setRedactionImageStrengthCommand ??= new RelayCommand<RedactionImageStrength>(
        strength => RedactionImageStrength = strength,
        _ => CanEditRedactions && IsRedactionImageTreated);

    /// <summary>
    /// The entry of the action bar and the button "Done" of the toolbar: starts the mode or leaves it like <see cref="RedactCommand"/>,
    /// but is never greyed out. Where the mode cannot start (no page shown yet, pages are organized, the file is encrypted) or cannot
    /// end (marks wait to be applied or discarded) it says why, as a hint over the document (<see cref="RedactionHint"/>), so a
    /// click is never without an answer. Executable while the tab has a document.
    /// </summary>
    public ICommand RedactEntryCommand => _redactEntryCommand ??= new RelayCommand(RedactFromEntry, () => !_disposed);

    /// <summary>
    /// The command "Mark selection" (catalog, Ctrl+Enter): turns the selected text into a mark, one for each page it covers, each
    /// with its text as the term and a rectangle for each line. Runs only in the mode with some text selected.
    /// </summary>
    public ICommand MarkSelectionCommand => _markSelectionCommand ??= new RelayCommand(() => _ = MarkSelectionAsync(), CanMarkSelection);

    /// <summary>
    /// Gets or sets how the document view drops a drag of the mode (an area being dragged out, a mark or one of its handles
    /// being dragged), set from the press to the release. <see cref="EscapeRedact"/> drops it before anything else; the function
    /// returns whether there was one.
    /// </summary>
    internal Func<bool>? CancelRedactionDrag { get; set; }

    /// <summary>
    /// Tells why the mode did not start although the user asked (the file is encrypted, or Pagewing could not check): the view
    /// shows it as a hint. Raised on the UI thread, for the request that ended without the mode and for no other.
    /// </summary>
    internal event Action<string>? RedactionHint;

    private void RedactFromEntry()
    {
        if (_isRedacting)
        {
            if (HasPendingRedactions)
            {
                RedactionHint?.Invoke(Strings.RedactMarksPending);
                return;
            }

            if (CanToggleRedact())
            {
                ToggleRedact();
            }

            return;
        }

        if (RedactionBlockedReason is { } reason)
        {
            RedactionHint?.Invoke(reason);
        }
        else if (CanBeginRedact())
        {
            _ = RequestRedactAsync();
        }
    }

    /// <summary>Gets whether a drag on a page may change the plan now: the mode is on and neither a card nor the update asks for the tab's quiet.</summary>
    internal bool CanMarkOnPage => CanEditRedactions;

    /// <summary>Gets the selected mark when exactly one is selected and it is an area mark (the only one with handles), else <see langword="null"/>.</summary>
    internal RedactionMark? SelectedAreaMark =>
        _selectedMarks.Count == 1 && _redactionHistory is { } history
            ? history.Current.Marks.FirstOrDefault(mark => mark.Kind == RedactionKind.Area && _selectedMarks.Contains(mark.Id))
            : null;

    /// <summary>Gets whether the selection holds a mark that arrow keys move: an area mark (see the remarks of the class).</summary>
    internal bool HasMovableMarkSelection =>
        _selectedMarks.Count > 0 && _redactionHistory is { } history
        && history.Current.Marks.Any(mark => mark.Kind == RedactionKind.Area && _selectedMarks.Contains(mark.Id));

    /// <summary>Gets whether "Mark selection" takes the selection now.</summary>
    private bool CanMarkSelection() => CanEditRedactions && HasTextSelection && PageTexts is not null;

    /// <summary>
    /// Makes marks of the selected text (see <see cref="MarkSelectionCommand"/>). All or nothing: if one page of the selection has
    /// no text that can be marked, nothing is marked and <see cref="RedactionNotice"/> says so, because a mark of only the other
    /// pages would leave the rest of what the user selected in the document. The selection goes when the marks are made.
    /// </summary>
    /// <returns>Completes when the marks are made or refused; never fails.</returns>
    internal async Task MarkSelectionAsync()
    {
        if (!CanMarkSelection() || TextSelection is not { IsEmpty: false } selection)
        {
            return;
        }

        var generation = _documentGeneration;
        var marks = new List<RedactionMark>();
        var refused = false;
        for (var page = selection.Start.Page; page <= selection.End.Page; page++)
        {
            if (await ReadTextAsync(generation, page) is not { } text)
            {
                // Another file is open now, or the page cannot be read: nothing is marked, and the selection stays.
                return;
            }

            var count = text.Chars.Count;
            var start = page == selection.Start.Page ? Math.Min(selection.Start.Caret, count) : 0;
            var end = page == selection.End.Page ? Math.Min(selection.End.Caret, count) : count;
            if (RedactionSearch.HitOfRange(text, page, start, end) is not { } hit)
            {
                // Only blanks on this page: nothing to remove there.
                continue;
            }

            if (!hit.Markable)
            {
                refused = true;
                continue;
            }

            marks.Add(new RedactionMark(Guid.NewGuid(), RedactionKind.Text, page, [.. hit.Areas], hit.Text, false));
        }

        if (_disposed || generation != _documentGeneration || !CanEditRedactions)
        {
            return;
        }

        if (refused || marks.Count == 0)
        {
            RedactionNotice = Strings.RedactSelectionNotMarkable;
            return;
        }

        if (StageRedaction(plan => plan.WithRange(marks), marks.Select(mark => mark.Id)))
        {
            TextSelection = null;
        }
    }

    /// <summary>
    /// Esc of the mode (the document view, the toolbar): drops a drag (<see cref="CancelRedactionDrag"/>), else clears the
    /// selected marks, else the text selection, else ends the mode, which it does only while no mark waits: a plan is applied or
    /// discarded, never lost by a key.
    /// </summary>
    /// <returns>Whether Esc had something to end.</returns>
    internal bool EscapeRedact()
    {
        if (!_isRedacting)
        {
            return false;
        }

        if (CancelRedactionDrag?.Invoke() == true)
        {
            return true;
        }

        if (_selectedMarks.Count > 0)
        {
            ClearMarkSelection();
            return true;
        }

        if (HasTextSelection)
        {
            TextSelection = null;
            return true;
        }

        if (!HasPendingRedactions)
        {
            DropRedactions();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gives the area marks their new rectangles in one step of the history (the release of a drag of a mark or of a handle).
    /// Marks that are not area marks, are not in the plan, or whose rectangle stays are left as they are; nothing changes when no
    /// mark does. A rectangle the plan refuses changes nothing and sets <see cref="RedactionNotice"/>.
    /// </summary>
    /// <param name="changes">The marks and the rectangles in points on their pages, origin top left.</param>
    /// <returns>Whether the plan changed.</returns>
    internal bool SetMarkAreas(IReadOnlyList<(Guid Id, PageRect Area)> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        return ChangeMarkAreas(changes, amend: false);
    }

    /// <summary>
    /// An arrow key on the selected area marks: moves them by <paramref name="dx"/>, <paramref name="dy"/> points (each -1, 0 or
    /// 1), or with <paramref name="resize"/> moves the lower right corner (the marks grow or shrink), kept on the page and at
    /// least <see cref="DrawingTransform.MinimumSize"/> wide and high. Presses in a row (at most <see cref="NudgeDelay"/> apart) on
    /// the same marks are one step.
    /// </summary>
    /// <returns>Whether a mark changed.</returns>
    internal bool NudgeMarks(int dx, int dy, bool resize)
    {
        if (!CanEditRedactions || (dx == 0 && dy == 0))
        {
            return false;
        }

        var plan = _redactionHistory!.Current;
        var changes = new List<(Guid Id, PageRect Area)>();
        var moved = new HashSet<Guid>();
        foreach (var mark in plan.Marks)
        {
            if (!_selectedMarks.Contains(mark.Id) || mark is not { Kind: RedactionKind.Area, Areas.Count: 1 })
            {
                continue;
            }

            moved.Add(mark.Id);
            changes.Add((mark.Id, NudgedArea(mark.Areas[0], PageBoundsOf(mark.Page), dx, dy, resize)));
        }

        if (changes.Count == 0)
        {
            return false;
        }

        var history = _redactionHistory;
        var now = SelectionClock.GetTimestamp();
        var amend = _markNudgeVersion == history.Version
            && _markNudgeMarks is { } previous && previous.SetEquals(moved)
            && SelectionClock.GetElapsedTime(_markNudgeStamp, now) <= NudgeDelay;
        var changed = ChangeMarkAreas(changes, amend);
        if (changed)
        {
            _markNudgeVersion = history.Version;
            _markNudgeStamp = now;
            _markNudgeMarks = moved;
        }

        return changed;
    }

    /// <summary>
    /// Selects the next (or previous) mark in reading order (page, then top, then left), starting from the single selected mark or,
    /// with none selected, at the first (last). After the last (before the first) mark the selection goes: the key is not taken
    /// and the focus moves on, so Tab never traps the keys in the document.
    /// </summary>
    /// <param name="forward">Whether to go forward (Tab) or back (Shift+Tab).</param>
    /// <returns>Whether a mark is selected now (the key is taken).</returns>
    internal bool WalkMarks(bool forward)
    {
        if (_redactionHistory is not { } history || history.Current.IsEmpty || !_isRedacting)
        {
            return false;
        }

        var ordered = history.Current.Marks
            .OrderBy(static mark => mark.Page)
            .ThenBy(static mark => mark.Areas.Min(area => area.Top))
            .ThenBy(static mark => mark.Areas.Min(area => area.Left))
            .ToList();
        var current = _selectedMarks.Count == 1 ? ordered.FindIndex(mark => _selectedMarks.Contains(mark.Id)) : -1;
        var next = current < 0 ? (forward ? 0 : ordered.Count - 1) : current + (forward ? 1 : -1);
        if (_selectedMarks.Count > 1)
        {
            // Several are selected (a search put them there): the walk starts at the end it goes toward.
            next = forward ? 0 : ordered.Count - 1;
        }

        if (next < 0 || next >= ordered.Count)
        {
            ClearMarkSelection();
            return false;
        }

        SetMarkSelection([ordered[next].Id]);
        return true;
    }

    /// <summary>The mode starts: the pointer drags out areas, and no run of arrow keys is waiting.</summary>
    private void ResetRedactionMarking()
    {
        _redactionTool = RedactionTool.Area;
        _markNudgeVersion = -1;
        _markNudgeMarks = null;
    }

    private void RaiseMarkingCanExecuteChanged()
    {
        _markSelectionCommand?.RaiseCanExecuteChanged();
        _chooseRedactionToolCommand?.RaiseCanExecuteChanged();
        _setRedactionFillCommand?.RaiseCanExecuteChanged();
    }

    private bool ChangeMarkAreas(IReadOnlyList<(Guid Id, PageRect Area)> changes, bool amend)
    {
        if (!CanEditRedactions)
        {
            return false;
        }

        // Only what really changes is a step: a drag that ends where it began, or an arrow against the edge of the page, is none.
        var current = _redactionHistory!.Current.Marks;
        var effective = new List<(RedactionMark Mark, PageRect Area)>();
        foreach (var (id, area) in changes)
        {
            var mark = current.FirstOrDefault(candidate => candidate.Id == id);
            if (mark is not { Kind: RedactionKind.Area, Areas.Count: 1 })
            {
                continue;
            }

            var normalized = new PageRect(
                Math.Min(area.Left, area.Right),
                Math.Min(area.Top, area.Bottom),
                Math.Max(area.Left, area.Right),
                Math.Max(area.Top, area.Bottom));
            if (mark.Areas[0] != normalized)
            {
                effective.Add((mark, normalized));
            }
        }

        if (effective.Count == 0)
        {
            return false;
        }

        return StageRedaction(
            plan =>
            {
                foreach (var (mark, area) in effective)
                {
                    plan = plan.Replace(mark with { Areas = [area] });
                }

                return plan;
            },
            null,
            amend);
    }

    // The page as shown, in points (the size the session has loaded for it), or null while it is not known.
    private PageRect? PageBoundsOf(int page)
    {
        var sizes = _session.LoadedSizes;
        return page >= 0 && page < sizes.Count && sizes[page].Width > 0 && sizes[page].Height > 0
            ? new PageRect(0, 0, sizes[page].Width, sizes[page].Height)
            : null;
    }

    private static PageRect NudgedArea(PageRect area, PageRect? bounds, int dx, int dy, bool resize)
    {
        const double min = DrawingTransform.MinimumSize;
        var left = Math.Min(area.Left, area.Right);
        var top = Math.Min(area.Top, area.Bottom);
        var right = Math.Max(area.Left, area.Right);
        var bottom = Math.Max(area.Top, area.Bottom);
        if (resize)
        {
            right = Math.Max(right + dx, left + min);
            bottom = Math.Max(bottom + dy, top + min);
            if (bounds is { } page)
            {
                right = Math.Min(right, Math.Max(page.Right, left + min));
                bottom = Math.Min(bottom, Math.Max(page.Bottom, top + min));
            }

            return new PageRect(left, top, right, bottom);
        }

        var width = right - left;
        var height = bottom - top;
        var x = left + dx;
        var y = top + dy;
        if (bounds is { } box)
        {
            // Kept on the page; a mark that already reaches beyond it (dragged there by an older version, or a page that changed
            // its size) only moves back toward it.
            x = dx < 0 ? Math.Max(x, Math.Min(box.Left, left)) : Math.Min(x, Math.Max(box.Right - width, left));
            y = dy < 0 ? Math.Max(y, Math.Min(box.Top, top)) : Math.Min(y, Math.Max(box.Bottom - height, top));
        }

        return new PageRect(x, y, x + width, y + height);
    }
}
