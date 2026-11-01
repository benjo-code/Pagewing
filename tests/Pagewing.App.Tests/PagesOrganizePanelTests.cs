using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The "Pages" panel in the mode "Organize pages" (milestone 13, task 7): the tools above the list, the list of the plan with
/// its selection, drag and drop, keyboard and marks, and what the panel leaves as it was outside the mode. The window is
/// headless; the plan itself is the subject of <see cref="PagesOrganizeModelTests"/>.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class PagesOrganizePanelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly string[] ToolButtons =
    [
        "RotateLeftButton", "RotateRightButton", "DeleteButton", "DuplicateButton", "BlankButton", "UndoButton", "RedoButton",
        "ApplyButton", "DiscardButton",
    ];

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public PagesOrganizePanelTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task ModeShowsTheToolbarOnlyWhileOrganizing()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await WaitForListAsync(window, tab);

        // Outside the mode: the list as before, one button to enter, none of the tools.
        var enter = Find<Button>(window, "OrganizeButton");
        Assert.True(enter.IsEffectivelyVisible);
        Assert.Null(FindOrNull<PagesOrganizeBar>(window, "OrganizeBar"));
        Assert.All(ToolButtons, name => Assert.Null(FindOrNull<Button>(window, name)));
        Assert.DoesNotContain("organizing", list.Classes);
        Assert.Equal(SelectionMode.Single, list.SelectionMode);
        Assert.Equal(6, list.Thumbnails.Count);
        Assert.All(list.Thumbnails, item => Assert.Equal(PageKind.Page, item.Kind));

        // Entering shows the tools and keeps the list; the entry button goes.
        Click(window, enter);
        Assert.True(tab.IsOrganizing);
        var bar = Find<PagesOrganizeBar>(window, "OrganizeBar");
        Assert.True(bar.IsEffectivelyVisible);
        Assert.All(ToolButtons, name => Assert.True(Find<Button>(bar, name).IsEffectivelyVisible, name));
        Assert.False(enter.IsEffectivelyVisible);
        Assert.Contains("organizing", list.Classes);
        Assert.NotEqual(SelectionMode.Single, list.SelectionMode);
        Assert.Equal(6, list.Thumbnails.Count);

        // The list has a context menu with the tools in the mode, and none outside it.
        var menu = Assert.IsType<ContextMenu>(list.ContextMenu);
        Assert.Equal(11, menu.Items.OfType<MenuItem>().Count());
        Assert.All(menu.Items.OfType<MenuItem>(), item => Assert.False(string.IsNullOrWhiteSpace(item.Header?.ToString())));

        // Nothing is staged yet: "Apply" waits, and the button that ends the mode is "Done" (nothing is lost by it).
        Assert.False(Find<Button>(bar, "ApplyButton").IsEffectivelyEnabled);
        Assert.Equal(Strings.PagesDone, Find<Button>(bar, "DiscardButton").GetValue(AutomationProperties.NameProperty));

        // Ending the mode takes the tools away and gives the list back as it was.
        Click(window, Find<Button>(bar, "DiscardButton"));
        Assert.False(tab.IsOrganizing);
        Assert.Null(FindOrNull<PagesOrganizeBar>(window, "OrganizeBar"));
        Assert.All(ToolButtons, name => Assert.Null(FindOrNull<Button>(window, name)));
        Assert.DoesNotContain("organizing", list.Classes);
        Assert.Equal(SelectionMode.Single, list.SelectionMode);
        Assert.Equal(6, list.Thumbnails.Count);
        Assert.Null(list.ContextMenu);
        Assert.True(enter.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task EnteringTheModeOpensThePagesPanel()
    {
        var (window, viewModel) = Show(800);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        Assert.Equal(SidePanelKind.None, tab.SidePanel);

        Press(window, Key.O, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.True(tab.IsOrganizing);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        await WaitForListAsync(window, tab);
        Assert.NotNull(FindOrNull<PagesOrganizeBar>(window, "OrganizeBar"));

        // With the panel closed the keys of the mode do nothing in the dark (Delete deletes no page nobody sees).
        tab.SelectPages([0]);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Press(window, Key.Delete);
        Assert.Equal(6, tab.PagePlan!.Slots.Count);
    }

    [AvaloniaFact]
    public async Task MultiSelectWithCtrlAndShift()
    {
        var (window, viewModel) = Show(1024, height: 1700);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await EnterAsync(window, tab);

        Click(window, Container(list, 0));
        Assert.Equal([0], tab.SelectedPagePositions.Order());
        Click(window, Container(list, 2), RawInputModifiers.Control);
        Assert.Equal([0, 2], tab.SelectedPagePositions.Order());
        Click(window, Container(list, 4), RawInputModifiers.Shift);
        Assert.Equal([2, 3, 4], tab.SelectedPagePositions.Order());
        Assert.All(list.Thumbnails.Select((item, index) => (item, index)), pair => Assert.Equal(pair.index is 2 or 3 or 4, pair.item.IsSelected));

        // Ctrl+click takes one out again; a plain click selects just that page.
        Click(window, Container(list, 3), RawInputModifiers.Control);
        Assert.Equal([2, 4], tab.SelectedPagePositions.Order());
        Click(window, Container(list, 1));
        Assert.Equal([1], tab.SelectedPagePositions.Order());

        // The selection of the tab (a command, undo) shows in the list.
        tab.SelectPages([0, 5]);
        Settle(window);
        Assert.Equal([0, 5], list.Selection.SelectedIndexes.Order());

        // A click on the page cursor is no jump in the document in this mode.
        var page = tab.CurrentPage;
        Click(window, Container(list, 0));
        Assert.Equal(page, tab.CurrentPage);

        // The selected page is marked by more than a colour: the frame, a check mark and the state for screen readers.
        var cell = Find<Border>(Container(list, 0), "ThumbnailCell");
        Assert.Contains("selected", cell.Classes);
        Assert.True(Find<Control>(Container(list, 0), "SelectedMark").IsEffectivelyVisible);
        Assert.False(Find<Control>(Container(list, 1), "SelectedMark").IsEffectivelyVisible);
        Assert.True(Container(list, 0).IsSelected);
    }

    [AvaloniaFact]
    public async Task DragMovesPagesAndShowsTheInsertionMarker()
    {
        var (window, viewModel) = Show(1024, height: 1700);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await EnterAsync(window, tab);

        // A press and a small wiggle is a click, not a drag.
        var start = CenterOf(window, Container(list, 0));
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(1, 1), RawInputModifiers.LeftMouseButton);
        Assert.Null(list.InsertionMarker);
        window.MouseUp(start + new Point(1, 1), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Equal([0], tab.SelectedPagePositions.Order());

        // Dragging page 1 into the lower half of page 2 shows the marker in the gap after page 2 (gap 2).
        var second = Container(list, 1);
        var target = CenterOf(window, second) + new Point(0, second.Bounds.Height * 0.4);
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, 12), RawInputModifiers.LeftMouseButton);
        window.MouseMove(target, RawInputModifiers.LeftMouseButton);
        Settle(window);
        Assert.True(list.IsDragging);
        Assert.Equal(2, list.InsertionGap);
        var marker = Assert.NotNull(list.InsertionMarker);
        var secondBottom = second.TranslatePoint(new Point(0, second.Bounds.Height), list)!.Value.Y;
        var thirdTop = Container(list, 2).TranslatePoint(default, list)!.Value.Y;
        Assert.InRange(marker.Center.Y, secondBottom, thirdTop);
        Assert.False(tab.HasPendingPageEdits, "nothing is staged before the drop");

        window.MouseUp(target, MouseButton.Left);
        Settle(window);
        Assert.False(list.IsDragging);
        Assert.Null(list.InsertionMarker);
        Assert.Equal([1, 0, 2, 3, 4, 5], tab.PagePlan!.Slots.Select(slot => slot.SourceIndex));
        Assert.Equal([1], tab.SelectedPagePositions.Order());
        Assert.Equal([1, 0, 2, 3, 4, 5], list.Thumbnails.Select(item => item.Slot!.SourceIndex));
        Assert.Equal(["1", "2", "3", "4", "5", "6"], list.Thumbnails.Select(item => item.PageText));

        // A selection of several pages travels together; Esc during the drag drops nothing.
        tab.SelectPages([0, 1]);
        Settle(window);
        var from = CenterOf(window, Container(list, 0));
        var to = CenterOf(window, Container(list, 3)) + new Point(0, Container(list, 3).Bounds.Height * 0.4);
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(0, 12), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Settle(window);
        Assert.Equal(4, list.InsertionGap);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(window);
        Assert.False(list.IsDragging);
        Assert.Null(list.InsertionMarker);
        window.MouseUp(to, MouseButton.Left);
        Settle(window);
        Assert.Equal([1, 0, 2, 3, 4, 5], tab.PagePlan.Slots.Select(slot => slot.SourceIndex));
        Assert.True(tab.IsOrganizing);

        // Without Esc the two pages move after page 4 and stay selected.
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Point(0, 12), RawInputModifiers.LeftMouseButton);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        window.MouseUp(to, MouseButton.Left);
        Settle(window);
        Assert.Equal([2, 3, 1, 0, 4, 5], tab.PagePlan.Slots.Select(slot => slot.SourceIndex));
        Assert.Equal([2, 3], tab.SelectedPagePositions.Order());
    }

    [AvaloniaFact]
    public async Task KeyboardOnlyUseWorks()
    {
        var (window, viewModel) = Show(1024, height: 1700);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await EnterAsync(window, tab);
        var slots = () => tab.PagePlan!.Slots.Select(slot => slot.SourceIndex).ToArray();

        // Select: an arrow selects the page, Shift+arrow adds the next one.
        Container(list, 0).Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Down);
        Assert.Equal([1], tab.SelectedPagePositions.Order());
        Press(window, Key.Down, RawInputModifiers.Shift);
        Assert.Equal([1, 2], tab.SelectedPagePositions.Order());

        // Move by one position (Ctrl+Shift+Down / Up), the pages stay selected.
        Press(window, Key.Down, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal([0, 3, 1, 2, 4, 5], slots());
        Assert.Equal([2, 3], tab.SelectedPagePositions.Order());
        Press(window, Key.Up, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal([0, 1, 2, 3, 4, 5], slots());
        Assert.False(tab.HasPendingPageEdits, "moving back is no change");

        // Rotate (Ctrl+Shift+Right), insert a blank page (Ctrl+Shift+B) and delete it again (Delete).
        Press(window, Key.Right, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal([90, 90], tab.PagePlan!.Slots.Where(slot => slot.ExtraRotation != 0).Select(slot => slot.ExtraRotation));
        Press(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(7, tab.PagePlan.Slots.Count);
        Assert.Equal(PageKind.Blank, list.Thumbnails[3].Kind);
        Press(window, Key.Delete);
        Assert.Equal(6, tab.PagePlan.Slots.Count);
        Press(window, Key.Z, RawInputModifiers.Control);
        Assert.Equal(7, tab.PagePlan.Slots.Count);
        Assert.Equal(7, list.Thumbnails.Count);

        // The buttons are reachable by Tab from the list: Shift+Tab leaves it backwards, and one press of Tab forwards leaves it
        // too (no trap). "Apply" and "Discard" work with the keys of a button.
        Container(list, 0).Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Tab, RawInputModifiers.Shift);
        var back = window.FocusManager!.GetFocusedElement();
        Assert.False(IsInside(list, back), "Shift+Tab leaves the list");
        Container(list, 0).Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Tab);
        Assert.False(IsInside(list, window.FocusManager.GetFocusedElement()), "Tab leaves the list");

        var applied = 0;
        tab.PageApplyRequested += () => applied++;
        var bar = Find<PagesOrganizeBar>(window, "OrganizeBar");
        var apply = Find<Button>(bar, "ApplyButton");
        Assert.True(apply.IsEffectivelyEnabled);
        apply.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Space);
        Assert.Equal(1, applied);

        // The window answers the request with the card of "Apply" (task 8); "Back" returns to the list.
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        Assert.Equal(PendingSaveKind.ApplyPages, viewModel.PendingSave!.Kind);
        viewModel.PendingSave.Abandon();
        await WaitUntilAsync(window, () => viewModel.PendingSave is null && !tab.IsSaving);

        var discard = Find<Button>(bar, "DiscardButton");
        discard.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter);
        var confirm = Find<Control>(bar, "DiscardConfirm");
        Assert.True(confirm.IsEffectivelyVisible);
        Assert.True(tab.IsOrganizing, "asked first, nothing is lost yet");
        Assert.True(tab.HasPendingPageEdits);
        Assert.Same(Find<Button>(bar, "KeepButton"), window.FocusManager!.GetFocusedElement());
        Press(window, Key.Escape);
        Assert.False(confirm.IsEffectivelyVisible);
        Assert.True(tab.IsOrganizing);

        discard.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter);
        Find<Button>(bar, "ConfirmDiscardButton").Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter);
        Assert.False(tab.IsOrganizing);
        Assert.False(tab.HasPendingPageEdits);
        Assert.Equal(6, list.Thumbnails.Count);
    }

    [AvaloniaFact]
    public async Task PageUpDownAndEscKeepTheirRules()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        var list = await EnterAsync(window, tab);
        tab.SelectPages([3]);
        tab.RotateSelectionCommand.Execute(90);
        list.ScrollIntoView(0);
        Settle(window);
        Container(list, 0).Focus(NavigationMethod.Tab);
        Settle(window);

        // Page Down moves the cursor of the list about a view and leaves the document where it is.
        Press(window, Key.PageDown);
        Assert.Equal(1, tab.CurrentPage);
        var focused = Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
        var index = list.IndexFromContainer(focused);
        Assert.True(index > 1, "Page Down moved by more than one entry");
        Assert.Equal([index], tab.SelectedPagePositions.Order());
        Press(window, Key.PageUp);
        Assert.True(list.IndexFromContainer(Assert.IsType<ListBoxItem>(window.FocusManager.GetFocusedElement())) < index);

        // Esc in the list ends neither the mode nor the plan (as in the other panels), and closes nothing else.
        Press(window, Key.Escape);
        Assert.True(tab.IsOrganizing);
        Assert.True(tab.HasPendingPageEdits);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);

        // The Esc of an overlay still comes first.
        viewModel.ShowHelpCommand.Execute(null);
        Settle(window);
        Assert.Equal(OverlayKind.Help, viewModel.Overlay);
        Press(window, Key.Escape);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.True(tab.IsOrganizing);
    }

    [AvaloniaFact]
    public async Task DeleteGoesNotToPagesWhileACardHasTheFocus()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await EnterAsync(window, tab);
        tab.SelectPages([1, 2]);
        Settle(window);

        // A list or a drop-down of a card has Delete for itself at most; the pages behind the card stay.
        viewModel.ShowSettingsCommand.Execute(null);
        Settle(window);
        var card = Find<SettingsCard>(window, "SettingsCard");
        var combo = Find<ComboBox>(card, "ThemeChoice");
        combo.Focus(NavigationMethod.Tab);
        Settle(window);
        Assert.Same(combo, window.FocusManager!.GetFocusedElement());
        Press(window, Key.Delete);
        Press(window, Key.Right, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(6, tab.PagePlan!.Slots.Count);
        Assert.True(tab.PagePlan.IsIdentity);
        Assert.False(tab.HasPendingPageEdits);

        // Back in the list the same key deletes.
        viewModel.HideOverlayCommand.Execute(null);
        Settle(window);
        Container(list, 1).Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Delete);
        Assert.Equal(4, tab.PagePlan.Slots.Count);
    }

    [AvaloniaFact]
    public async Task RotatedPageIsDrawnRotatedAndLabelledWithTextNotColour()
    {
        var (window, viewModel) = Show(1024, height: 1700);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await EnterAsync(window, tab);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null && list.Thumbnails[2].Bitmap is not null);
        var upright = list.Thumbnails[0];
        Assert.Equal(ThumbnailPlanner.Width, upright.FrameWidth, 0.5);
        var tall = upright.FrameHeight;
        Assert.True(tall > upright.FrameWidth);
        Assert.False(Find<Control>(Container(list, 0), "RotatedMark").IsEffectivelyVisible);

        // A portrait page turned by 90 degrees is shown on its side, no wider than the column, and says so in words.
        tab.SelectPages([0, 2]);
        tab.RotateSelectionCommand.Execute(90);
        Settle(window);
        var item = list.Thumbnails[0];
        Assert.Equal(90, item.Rotation);
        Assert.Equal(ThumbnailPlanner.Width, item.FrameWidth, 0.5);
        Assert.Equal(ThumbnailPlanner.Width * ThumbnailPlanner.Width / tall, item.FrameHeight, 0.5);
        Assert.True(item.FrameWidth > item.FrameHeight);
        var container = Container(list, 0);
        var frame = Find<Control>(container, "ThumbnailFrame");
        Assert.Equal(item.FrameWidth, frame.Bounds.Width, 1.0);
        Assert.Equal(item.FrameHeight, frame.Bounds.Height, 1.0);
        var image = Find<Image>(container, "ThumbnailImage");
        Assert.Equal(item.ImageWidth, image.Width, 0.5);
        Assert.Equal(item.ImageHeight, image.Height, 0.5);
        var turn = Assert.IsType<RotateTransform>(image.RenderTransform);
        Assert.Equal(90, turn.Angle);
        Assert.NotNull(item.Bitmap);

        // A landscape page turned the same way stands up, as wide as it was high, never enlarged.
        var landscape = list.Thumbnails[2];
        Assert.Equal(90, landscape.Rotation);
        Assert.True(landscape.FrameHeight > landscape.FrameWidth);
        Assert.Equal(ThumbnailPlanner.Width, landscape.FrameHeight, 0.5);

        // The mark is a symbol and a text, and the name for screen readers has it too.
        var mark = Find<Control>(container, "RotatedMark");
        Assert.True(mark.IsEffectivelyVisible);
        var text = Find<TextBlock>(container, "RotationText");
        Assert.Equal("Rotated 90°", text.Text);
        Assert.True(text.IsEffectivelyVisible);
        Assert.NotNull(Find<StrokeIcon>(mark, "RotatedIcon").Data);
        Assert.Contains("Rotated 90°", AutomationProperties.GetName(container));

        // Another turn changes the words, a full turn takes them away.
        tab.RotateSelectionCommand.Execute(90);
        Settle(window);
        Assert.Equal("Rotated 180°", Find<TextBlock>(container, "RotationText").Text);
        Assert.Equal(180, Assert.IsType<RotateTransform>(Find<Image>(container, "ThumbnailImage").RenderTransform).Angle);
        Assert.Equal(upright.FrameWidth, list.Thumbnails[0].FrameWidth, 0.5);
        tab.RotateSelectionCommand.Execute(180);
        Settle(window);
        Assert.False(Find<Control>(container, "RotatedMark").IsEffectivelyVisible);

        // A blank page is drawn, not rendered, in the size of its neighbour, and labelled "Blank page".
        tab.SelectPages([0]);
        tab.InsertBlankPageCommand.Execute(null);
        Settle(window);
        var blank = list.Thumbnails[1];
        Assert.Equal(PageKind.Blank, blank.Kind);
        Assert.Null(blank.Bitmap);
        Assert.Equal(ThumbnailPlanner.Width, blank.FrameWidth, 0.5);
        var blankContainer = Container(list, 1);
        Assert.True(Find<Control>(blankContainer, "BlankFace").IsEffectivelyVisible);
        Assert.Equal("Blank page", Find<TextBlock>(blankContainer, "BlankText").Text);
        Assert.True(Find<Control>(blankContainer, "BlankMark").IsEffectivelyVisible);
        Assert.False(Find<Image>(blankContainer, "ThumbnailImage").IsEffectivelyVisible);
        Assert.Contains("Blank page", AutomationProperties.GetName(blankContainer));
        Assert.DoesNotContain(list.LastRequests, request => request.Key.Page == -1);
    }

    [AvaloniaFact]
    public async Task PagesOfTheSamePageShareOneRenderedThumbnail()
    {
        var (window, viewModel) = Show(1024, height: 1700);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await EnterAsync(window, tab);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        tab.SelectPages([0]);
        tab.DuplicateSelectionCommand.Execute(null);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null && list.Thumbnails[1].Bitmap is not null);

        // The copy and the original are two entries with their own bitmaps of the same rendering (one request).
        Assert.Equal(0, list.Thumbnails[0].Slot!.SourceIndex);
        Assert.Equal(0, list.Thumbnails[1].Slot!.SourceIndex);
        Assert.NotSame(list.Thumbnails[0].Bitmap, list.Thumbnails[1].Bitmap);
        Assert.Equal(list.LastRequests.Select(request => request.Key.Page).Distinct().Count(), list.LastRequests.Count);

        // Deleting the original keeps the copy's picture on screen.
        tab.SelectPages([0]);
        tab.DeletePagesCommand.Execute(null);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        Assert.Equal(6, list.Thumbnails.Count);
    }

    [AvaloniaFact]
    public async Task CounterShowsOldAndNewPageCount()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await EnterAsync(window, tab);
        var bar = Find<PagesOrganizeBar>(window, "OrganizeBar");
        var counter = Find<TextBlock>(bar, "PageCounter");
        var pending = Find<Control>(bar, "PendingNote");
        Assert.Equal("6 pages", counter.Text);
        Assert.False(pending.IsEffectivelyVisible);

        tab.SelectPages([0, 1]);
        tab.DeletePagesCommand.Execute(null);
        Settle(window);
        Assert.Equal("6 → 4 pages", counter.Text);
        Assert.Equal(4, list.Thumbnails.Count);

        // The state is told in words and by a symbol, not by a colour: a note, and the name of the tab.
        Assert.True(pending.IsEffectivelyVisible);
        Assert.Equal(Strings.PageEditsPending, Find<TextBlock>(bar, "PendingText").Text);
        Assert.NotNull(Find<StrokeIcon>(bar, "PendingIcon").Data);
        Assert.Contains("page changes", tab.TabAutomationName);

        // The new count follows an undo and a redo; one page is "page", not "pages".
        tab.UndoPagesCommand.Execute(null);
        Settle(window);
        Assert.Equal("6 pages", counter.Text);
        Assert.False(pending.IsEffectivelyVisible);
        Assert.DoesNotContain("page changes", tab.TabAutomationName);
        tab.SelectPages([0, 1, 2, 3, 4]);
        tab.DeletePagesCommand.Execute(null);
        Settle(window);
        Assert.Equal("6 → 1 page", counter.Text);

        // A refused change says why, next to the tools, and can be closed.
        tab.SelectPages([0]);
        tab.DeletePagesCommand.Execute(null);
        Settle(window);
        var notice = Find<TextBlock>(bar, "NoticeText");
        Assert.Equal(Strings.PageEditsLastPage, notice.Text);
        Assert.True(notice.IsEffectivelyVisible);
        Click(window, Find<Button>(bar, "NoticeCloseButton"));
        Assert.False(notice.IsEffectivelyVisible);

        Strings.Culture = new CultureInfo("de");
        tab.UndoPagesCommand.Execute(null);
        Settle(window);
        Assert.Equal("6 Seiten", tab.PageCounterText);
        tab.RedoPagesCommand.Execute(null);
        Assert.Equal("6 → 1 Seite", tab.PageCounterText);
    }

    [AvaloniaFact]
    public async Task ThousandPagesStillScroll()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        var list = await EnterAsync(window, tab);
        await WaitUntilAsync(window, () => list.LastRequests.Count > 0);

        // Only the entries that exist are planned: a handful of the thousand, at most the planner's limit.
        Assert.Equal(1000, list.Thumbnails.Count);
        var realized = RealizedIndexes(list);
        Assert.InRange(realized.Count, 1, 100);
        Assert.InRange(list.LastRequests.Count, 1, ThumbnailPlanner.MaxRequests);
        Assert.All(list.LastRequests, request => Assert.Contains(request.Key.Page, realized));

        // Scrolling brings others, and a staged change keeps it so: pages 700 to 702 turned, one deleted far above them.
        tab.SelectPages([700, 701, 702]);
        tab.RotateSelectionCommand.Execute(90);
        tab.SelectPages([5]);
        tab.DeletePagesCommand.Execute(null);
        Settle(window);
        Assert.Equal(999, list.Thumbnails.Count);
        list.ScrollIntoView(699);
        await WaitUntilAsync(window, () => list.LastRequests.Any(request => request.Key.Page == 700));
        realized = RealizedIndexes(list);
        Assert.InRange(realized.Count, 1, 100);
        Assert.InRange(list.LastRequests.Count, 1, ThumbnailPlanner.MaxRequests);

        // The positions after the deletion are the plan's: the entry at 699 shows page 701 of the document (source index 700).
        Assert.Equal(700, list.Thumbnails[699].Slot!.SourceIndex);
        Assert.Equal(90, list.Thumbnails[699].Rotation);
        Assert.Equal("700", list.Thumbnails[699].PageText);
        await WaitUntilAsync(window, () => list.Thumbnails[699].Bitmap is not null);
        Assert.All(list.LastRequests, request => Assert.Contains(request.Key.Page, list.Thumbnails.Select(item => item.Slot!.SourceIndex)));

        // Entries that are not realized hold no bitmap.
        realized = RealizedIndexes(list);
        for (var index = 0; index < list.Thumbnails.Count; index++)
        {
            if (!realized.Contains(index))
            {
                Assert.Null(list.Thumbnails[index].Bitmap);
            }
        }

        // The mode ends: the list is the document's again, 1000 pages.
        tab.DiscardPageEditsCommand.Execute(null);
        Settle(window);
        Assert.Equal(1000, list.Thumbnails.Count);
        Assert.All(list.Thumbnails.Take(10), item => Assert.Equal(PageKind.Page, item.Kind));
    }

    [AvaloniaFact]
    public async Task AutomationNamesExistForEveryButton()
    {
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            var (window, viewModel) = Show(1024);
            var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
            await EnterAsync(window, tab);
            tab.SelectPages([0]);
            tab.DeletePagesCommand.Execute(null);
            tab.SelectPages([0]);
            tab.DeletePagesCommand.Execute(null);
            tab.SelectPages([0, 1, 2, 3]);
            tab.DeletePagesCommand.Execute(null);
            Settle(window);

            var bar = Find<PagesOrganizeBar>(window, "OrganizeBar");
            var buttons = bar.GetVisualDescendants().OfType<Button>().ToList();
            Assert.True(buttons.Count >= ToolButtons.Length, language);
            foreach (var button in buttons)
            {
                var name = AutomationProperties.GetName(button);
                Assert.False(string.IsNullOrWhiteSpace(name), $"{language}: {button.Name} has no name");
            }

            // The list, the bar and the entry button have names, the entries carry the page.
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(bar)));
            Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(bar));
            var list = Find<ThumbnailList>(window, "ThumbnailList");
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(list)));
            Assert.StartsWith(Strings.ThumbnailName.Replace("{0}", "1", StringComparison.Ordinal), AutomationProperties.GetName(Container(list, 0)), StringComparison.Ordinal);

            // The tooltips of the tools name the shortcut where there is one.
            var keys = GestureText.Of(viewModel.Commands.ById("pages.rotateLeft")!.Gesture!);
            Assert.Contains(keys, ToolTip.GetTip(Find<Button>(bar, "RotateLeftButton"))?.ToString());
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void ColoursMeetAA(string theme)
    {
        var variant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "HighContrast" => HighContrast.Variant,
            _ => ThemeVariant.Light,
        };
        double Ratio(string foreground, string background) => Wcag.Contrast(TokenTests.Token(foreground, variant), TokenTests.Token(background, variant));

        // The panel and its bar are surface.sidebar: the counter and the marks, the pending note, the icons and the notice.
        Assert.True(Ratio("Text.Primary", "Surface.Sidebar") >= 4.5, "text.primary on surface.sidebar");
        Assert.True(Ratio("Text.Secondary", "Surface.Sidebar") >= 4.5, "text.secondary mark text on surface.sidebar");
        Assert.True(Ratio("Accent.Default", "Surface.Sidebar") >= 3, "accent.default insertion marker and ring on surface.sidebar");
        Assert.True(Ratio("Danger.Default", "Surface.Sidebar") >= 3, "danger.default icon of the notice on surface.sidebar");
        Assert.True(Ratio("Border.Strong", "Surface.Sidebar") >= 3, "border.strong on surface.sidebar");
        Assert.True(Ratio("Focus.Ring", "Surface.Sidebar") >= 3, "focus.ring on surface.sidebar");

        // A selected entry: accent.subtle with the ring, the number, the marks, the check mark.
        Assert.True(Ratio("Text.Primary", "Accent.Subtle") >= 4.5, "text.primary number on accent.subtle");
        Assert.True(Ratio("Text.Secondary", "Accent.Subtle") >= 4.5, "text.secondary mark text on accent.subtle");
        Assert.True(Ratio("Accent.Default", "Accent.Subtle") >= 3, "accent.default ring on accent.subtle");
        Assert.True(Ratio("Accent.On", "Accent.Default") >= 4.5, "accent.on check mark on accent.default");
        Assert.True(Ratio("Focus.Ring", "Accent.Subtle") >= 3, "focus.ring on accent.subtle");

        // The empty frame of a page that is not drawn yet and the frame of a blank page.
        Assert.True(Ratio("Text.Secondary", "Surface.Raised") >= 4.5, "text.secondary icon on surface.raised");
    }

    [AvaloniaFact]
    public void TextsExistInBothLanguages()
    {
        var texts = new Func<string>[]
        {
            () => Strings.PagesOrganizeBar,
            () => Strings.PagesDone,
            () => Strings.PagesApply,
            () => Strings.PagesDiscard,
            () => Strings.PagesDiscardAsk,
            () => Strings.PagesDiscardConfirm,
            () => Strings.PagesKeep,
            () => Strings.PagesNoticeClose,
            () => Strings.PageMarkerBlank,
            () => Strings.PageMarkerOtherFile,
            () => Strings.PageMarkerCopy,
            () => Strings.PageMarkerRotated,
            () => Strings.PageCounterSame,
            () => Strings.PageCounterSameOne,
            () => Strings.PageCounterChange,
            () => Strings.PageCounterChangeOne,
            () => Strings.TabPageEditsSuffix,
            () => Strings.CommandMovePagesEarlier,
            () => Strings.CommandMovePagesLater,
            () => Strings.CommandApplyPageEdits,
            () => Strings.DiscardPageEditsText,
        };
        var english = new List<string>();
        var german = new List<string>();
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            var read = texts.Select(text => text()).ToList();
            Assert.All(read, value => Assert.False(string.IsNullOrWhiteSpace(value)));
            (language == "en" ? english : german).AddRange(read);
        }

        // Every one is translated (none is the English text in the German file), except words that are the same in both.
        for (var i = 0; i < texts.Length; i++)
        {
            if (english[i] != german[i])
            {
                continue;
            }

            Assert.True(english[i] is "Alt", $"text {i} is not translated: {english[i]}");
        }

        // The neutral wording of the question about discarding (it is also asked on the way to an update).
        Strings.Culture = new CultureInfo("de");
        Assert.Contains("Diese Änderungen gehen verloren.", Strings.DiscardPageEditsText, StringComparison.Ordinal);
        Strings.Culture = new CultureInfo("en");
        Assert.DoesNotContain("close now", Strings.DiscardPageEditsText, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task CompactDensityKeepsTheModeUsable()
    {
        var (window, viewModel) = Show(1024, height: 1700);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await WaitForListAsync(window, tab);
        list.Columns = 2;
        list.ThumbnailWidth = 72;
        Settle(window);
        tab.OrganizeCommand.Execute(null);
        Settle(window);
        Assert.True(tab.IsOrganizing);
        tab.SelectPages([1]);
        tab.RotateSelectionCommand.Execute(90);
        Settle(window);
        Assert.Equal(6, list.Thumbnails.Count);
        Assert.All(list.Thumbnails, item => Assert.True(item.FrameWidth <= list.FrameWidth + 0.5));

        // The marker for a gap lies beside the entry in two columns.
        var first = Container(list, 0);
        var target = CenterOf(window, Container(list, 1)) + new Point(Container(list, 1).Bounds.Width * 0.4, 0);
        tab.SelectPages([0]);
        Settle(window);
        window.MouseDown(CenterOf(window, first), MouseButton.Left);
        window.MouseMove(CenterOf(window, first) + new Point(0, 12), RawInputModifiers.LeftMouseButton);
        window.MouseMove(target, RawInputModifiers.LeftMouseButton);
        Settle(window);
        Assert.Equal(2, list.InsertionGap);
        var marker = Assert.NotNull(list.InsertionMarker);
        Assert.True(marker.Height > marker.Width, "a vertical bar between two columns");
        window.MouseUp(target, MouseButton.Left);
        Settle(window);
        Assert.Equal([1, 0, 2, 3, 4, 5], tab.PagePlan!.Slots.Select(slot => slot.SourceIndex));
    }

    [AvaloniaFact]
    public async Task MovingTheSelectionByOneFollowsTheRules()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        await EnterAsync(window, tab);

        // Nothing to move without a selection, or past the ends.
        Assert.False(tab.MoveSelectionEarlierCommand.CanExecute(null));
        tab.SelectPages([0]);
        Assert.False(tab.MoveSelectionEarlierCommand.CanExecute(null));
        Assert.True(tab.MoveSelectionLaterCommand.CanExecute(null));
        tab.SelectPages([5]);
        Assert.False(tab.MoveSelectionLaterCommand.CanExecute(null));

        // A split selection gathers at the page it moves past.
        tab.SelectPages([1, 3]);
        tab.MoveSelectionLaterCommand.Execute(null);
        Assert.Equal([0, 2, 4, 1, 3, 5], tab.PagePlan!.Slots.Select(slot => slot.SourceIndex));
        Assert.Equal([3, 4], tab.SelectedPagePositions.Order());
        tab.MoveSelectionEarlierCommand.Execute(null);
        Assert.Equal([0, 2, 1, 3, 4, 5], tab.PagePlan.Slots.Select(slot => slot.SourceIndex));
        Assert.Equal([2, 3], tab.SelectedPagePositions.Order());

        // The catalog has the keys and the apply command, and runs them only in the mode.
        var catalog = viewModel.Commands;
        Assert.Equal("Ctrl+Shift+Up", catalog.ById("pages.moveEarlier")!.Gesture!.ToString());
        Assert.Equal("Ctrl+Shift+Down", catalog.ById("pages.moveLater")!.Gesture!.ToString());
        var applied = 0;
        tab.PageApplyRequested += () => applied++;
        Assert.True(catalog.ById("pages.apply")!.CanExecute());
        catalog.ById("pages.apply")!.Execute();
        Assert.Equal(1, applied);

        // The window answers the request with the card of "Apply" (task 8); "Back" returns to the mode.
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        viewModel.PendingSave!.Abandon();
        await WaitUntilAsync(window, () => viewModel.PendingSave is null && !tab.IsSaving);
        tab.DiscardPageEditsCommand.Execute(null);
        Assert.False(catalog.ById("pages.apply")!.CanExecute());
        Assert.False(tab.ApplyPageEditsCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task EnteringNeverMakesTheBarForAnotherDocument()
    {
        // The bar and the mode's list come only with the mode: a panel built without it has neither.
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        await WaitForListAsync(window, tab);
        Assert.Null(FindOrNull<PagesOrganizeBar>(window, "OrganizeBar"));
        Assert.False(tab.HasPageEditCommands);
        Assert.Null(tab.PageHistory);
    }

    [AvaloniaFact]
    public async Task PlainListEntriesKeepTheirHeight()
    {
        // Outside the mode (and for an entry without a mark inside it) an entry is what it was before the marks existed: the
        // cell's padding, the ring with the frame, the 6 DIP between them and the number, and nothing else.
        var (window, viewModel) = Show(1024, height: 1700);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var list = await WaitForListAsync(window, tab);
        double ExpectedHeight(ListBoxItem container)
        {
            var cell = Find<Border>(container, "ThumbnailCell");
            return cell.Padding.Top + cell.Padding.Bottom + Find<Border>(container, "CurrentRing").Bounds.Height + 6 + Find<TextBlock>(container, "PageNumber").Bounds.Height;
        }

        for (var index = 0; index < 3; index++)
        {
            var container = Container(list, index);
            Assert.False(Find<Control>(container, "Marks").IsVisible, "no row of marks in the plain list");
            Assert.Equal(ExpectedHeight(container), Find<Border>(container, "ThumbnailCell").Bounds.Height, 0.5);
        }

        var first = Container(list, 0);
        var second = Container(list, 1);
        Assert.Equal(first.Bounds.Height + 16, second.Bounds.Y - first.Bounds.Y, 0.5);

        // In the mode an entry without a mark is as high; one with a mark has the row of marks besides.
        tab.OrganizeCommand.Execute(null);
        await WaitUntilAsync(window, () => list.Classes.Contains("organizing"));
        var plain = Container(list, 1);
        Assert.Equal(ExpectedHeight(plain), Find<Border>(plain, "ThumbnailCell").Bounds.Height, 0.5);
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        Settle(window);
        var marked = Container(list, 0);
        Assert.True(Find<Control>(marked, "Marks").IsVisible);
        Assert.True(Find<Border>(marked, "ThumbnailCell").Bounds.Height > ExpectedHeight(marked), "the marks take room");
    }

    [AvaloniaFact]
    public async Task EveryMarkIsNamedInTextAndInTheEntryName()
    {
        var (window, viewModel) = Show(1024, height: 1700);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        var list = await EnterAsync(window, tab);

        // A duplicate: the later entry of the page is the copy, the first is not.
        tab.SelectPages([0]);
        tab.DuplicateSelectionCommand.Execute(null);
        Settle(window);
        Assert.False(list.Thumbnails[0].IsCopy);
        Assert.True(list.Thumbnails[1].IsCopy);
        var copy = Container(list, 1);
        Assert.True(Find<Control>(copy, "CopyMark").IsEffectivelyVisible);
        Assert.Equal("Copy", Find<TextBlock>(copy, "CopyText").Text);
        Assert.Contains(", Copy", AutomationProperties.GetName(copy));
        Assert.DoesNotContain("Copy", AutomationProperties.GetName(Container(list, 0)));
        Assert.True(Find<Control>(copy, "Marks").IsVisible);

        // Delete the first of the two: the one that is left is no copy any more.
        tab.SelectPages([0]);
        tab.DeletePagesCommand.Execute(null);
        Settle(window);
        Assert.False(list.Thumbnails[0].IsCopy);
        Assert.False(Find<Control>(Container(list, 0), "Marks").IsVisible);

        // Turned, blank and from another file: each has its words in the name of the entry.
        tab.SelectPages([1]);
        tab.RotateSelectionCommand.Execute(180);
        tab.SelectPages([2]);
        tab.InsertBlankPageCommand.Execute(null);
        Settle(window);
        Assert.Contains(", Rotated 180°", AutomationProperties.GetName(Container(list, 1)));
        var blankIndex = list.Thumbnails.ToList().FindIndex(item => item.Kind == PageKind.Blank);
        Assert.Contains(", Blank page", AutomationProperties.GetName(Container(list, blankIndex)));

        // A page of another file (the plan is given one the way the later insert command will): a stand-in tile and its words.
        var history = tab.PageHistory!;
        history.Do(history.Current.InsertFrom(tab.AllocatePageSource(), [0], 1));
        history.Undo();
        tab.RedoPagesCommand.Execute(null);
        Settle(window);
        var foreign = Container(list, 1);
        Assert.Equal(PageKind.Foreign, list.Thumbnails[1].Kind);
        Assert.True(Find<Control>(foreign, "ForeignMark").IsEffectivelyVisible);
        Assert.True(Find<Control>(foreign, "ForeignFace").IsEffectivelyVisible);
        Assert.Equal("From another file", Find<TextBlock>(foreign, "ForeignText").Text);
        Assert.Contains(", From another file", AutomationProperties.GetName(foreign));
        Assert.False(Find<Image>(foreign, "ThumbnailImage").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ApplyAndDiscardWorkWithoutThePanelButNotBehindACard()
    {
        var (window, viewModel) = Show(800);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        await EnterAsync(window, tab);
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        var applied = 0;
        tab.PageApplyRequested += () => applied++;
        var catalog = viewModel.Commands;

        // With the panel closed the pages cannot be changed, but the mode can still be applied or discarded.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.False(catalog.ById("pages.rotateLeft")!.CanExecute());
        Assert.False(catalog.ById("pages.delete")!.CanExecute());
        Assert.True(catalog.ById("pages.apply")!.CanExecute());
        Assert.True(catalog.ById("pages.discard")!.CanExecute());
        catalog.ById("pages.apply")!.Execute();
        Assert.Equal(1, applied);

        // The window answers the request with the card of "Apply" (task 8); "Back" returns to the mode.
        await WaitUntilAsync(window, () => viewModel.PendingSave is not null);
        viewModel.PendingSave!.Abandon();
        await WaitUntilAsync(window, () => viewModel.PendingSave is null && !tab.IsSaving);

        // A card over the window blocks them too.
        viewModel.ShowSettingsCommand.Execute(null);
        Settle(window);
        Assert.False(catalog.ById("pages.apply")!.CanExecute());
        Assert.False(catalog.ById("pages.discard")!.CanExecute());
        viewModel.HideOverlayCommand.Execute(null);
        Settle(window);

        catalog.ById("pages.discard")!.Execute();
        Assert.False(tab.IsOrganizing);
        Assert.False(tab.HasPendingPageEdits);
    }

    [AvaloniaFact]
    public async Task UndoAndRedoOfThePlanNeedThePanelAndNoCard()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "pages-marked.pdf");
        await EnterAsync(window, tab);
        tab.SelectPages([0]);
        tab.RotateSelectionCommand.Execute(90);
        Assert.True(tab.HasPendingPageEdits);
        var catalog = viewModel.Commands;
        Assert.True(catalog.ById("edit.undo")!.CanExecute());

        // A card with a drop-down: Ctrl+Z reaches the card, not the plan behind it.
        viewModel.ShowSettingsCommand.Execute(null);
        Settle(window);
        var combo = Find<ComboBox>(Find<SettingsCard>(window, "SettingsCard"), "ThemeChoice");
        combo.Focus(NavigationMethod.Tab);
        Settle(window);
        Assert.False(catalog.ById("edit.undo")!.CanExecute());
        Press(window, Key.Z, RawInputModifiers.Control);
        Assert.True(tab.HasPendingPageEdits);
        viewModel.HideOverlayCommand.Execute(null);
        Settle(window);

        // With the panel closed neither key changes the plan, nor does the tab's own command.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.False(catalog.ById("edit.undo")!.CanExecute());
        Press(window, Key.Z, RawInputModifiers.Control);
        tab.UndoCommand.Execute(null);
        Assert.True(tab.HasPendingPageEdits);

        // With the panel open the undo works, and so does the redo.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        await WaitForListAsync(window, tab);
        Press(window, Key.Z, RawInputModifiers.Control);
        Assert.False(tab.HasPendingPageEdits);
        Assert.True(catalog.ById("edit.redo")!.CanExecute());
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        Assert.False(catalog.ById("edit.redo")!.CanExecute());
        Press(window, Key.Y, RawInputModifiers.Control);
        Assert.False(tab.HasPendingPageEdits);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        await WaitForListAsync(window, tab);
        Press(window, Key.Y, RawInputModifiers.Control);
        Assert.True(tab.HasPendingPageEdits);
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(double width, double height = 720)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = height, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
    }

    /// <summary>Opens a test file in a new tab and waits until the tab has shown its first sharp page; returns that tab.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage);
        return tab;
    }

    /// <summary>Enters the mode (the panel opens with it) and waits until the list shows the plan.</summary>
    private static async Task<ThumbnailList> EnterAsync(Window window, TabViewModel tab)
    {
        if (tab.SidePanel != SidePanelKind.Pages)
        {
            tab.SidePanel = SidePanelKind.Pages;
        }

        await WaitForListAsync(window, tab);
        tab.OrganizeCommand.Execute(null);
        Assert.True(tab.IsOrganizing);
        var list = await WaitForListAsync(window, tab);
        await WaitUntilAsync(window, () => list.Classes.Contains("organizing"));
        return list;
    }

    private static async Task<ThumbnailList> WaitForListAsync(Window window, TabViewModel tab)
    {
        ThumbnailList? list = null;
        await WaitUntilAsync(window, () =>
        {
            list = FindOrNull<ThumbnailList>(window, "ThumbnailList");
            return list is { IsEffectivelyVisible: true }
                && ReferenceEquals(list.DataContext, tab)
                && list.Thumbnails.Count == (tab.PagePlan?.Slots.Count ?? tab.PageCount)
                && list.GetSelfAndVisualAncestors().OfType<Visual>().All(visual => visual.Opacity >= 1);
        });
        return list!;
    }

    private static ListBoxItem Container(ThumbnailList list, int index)
    {
        list.ScrollIntoView(index);
        list.UpdateLayout();
        return Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
    }

    private static bool IsInside(Visual ancestor, object? element) =>
        element is Visual visual && visual.GetSelfAndVisualAncestors().Contains(ancestor);

    private static HashSet<int> RealizedIndexes(ThumbnailList list) =>
        list.GetRealizedContainers().Select(list.IndexFromContainer).Where(index => index >= 0).ToHashSet();

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        var point = CenterOf(window, control);
        window.MouseDown(point, MouseButton.Left, modifiers);
        window.MouseUp(point, MouseButton.Left, modifiers);
        Settle(window);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKeyOf(key), null);
        window.KeyRelease(key, modifiers, PhysicalKeyOf(key), null);
        Settle(window);
    }

    private static PhysicalKey PhysicalKeyOf(Key key) => key switch
    {
        Key.Down => PhysicalKey.ArrowDown,
        Key.Up => PhysicalKey.ArrowUp,
        Key.Left => PhysicalKey.ArrowLeft,
        Key.Right => PhysicalKey.ArrowRight,
        Key.Enter => PhysicalKey.Enter,
        Key.Space => PhysicalKey.Space,
        Key.Tab => PhysicalKey.Tab,
        Key.Escape => PhysicalKey.Escape,
        Key.Delete => PhysicalKey.Delete,
        Key.PageDown => PhysicalKey.PageDown,
        Key.PageUp => PhysicalKey.PageUp,
        Key.B => PhysicalKey.B,
        Key.O => PhysicalKey.O,
        Key.Z => PhysicalKey.Z,
        Key.Y => PhysicalKey.Y,
        _ => PhysicalKey.None,
    };

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The window did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
