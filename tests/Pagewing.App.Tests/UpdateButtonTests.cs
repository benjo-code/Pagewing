using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Updates;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The button "Update" in the title bar (design system 7.11): hidden without an offer, icon and text with one, only the
/// icon below 900 px, named for screen readers, reachable by keyboard, and it never covers the caption buttons. The
/// offer comes from a fake source through the real coordinator; nothing touches the network.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateButtonTests : UpdateTestHost
{
    [AvaloniaFact]
    public async Task HiddenWithoutAnUpdate()
    {
        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => Source.FetchCalls == 1);
        await RunForAsync(window, 200);

        Assert.False(viewModel.Update?.ShowButton ?? false);
        Assert.False(Find<Button>(window, "UpdateButton").IsEffectivelyVisible);
        Assert.Null(TryFind<UpdateCard>(window, "UpdateCard"));
    }

    [AvaloniaFact]
    public async Task VisibleWithAnOfferAndShowsIconAndText()
    {
        var (window, viewModel) = await ShowOfferAsync();
        var button = Find<Button>(window, "UpdateButton");

        Assert.True(button.IsEffectivelyVisible);
        Assert.Contains("update", button.Classes);

        // The state is said in words, not only by the accent colour: the download icon and the text "Update".
        Assert.True(Find<StrokeIcon>(button, "UpdateButtonIcon").IsEffectivelyVisible);
        var text = Find<TextBlock>(button, "UpdateButtonText");
        Assert.True(text.IsEffectivelyVisible);
        Assert.Equal(Strings.UpdateButton, text.Text);
        Assert.Equal("Update", text.Text);
        Assert.Equal(UpdateStage.Available, viewModel.Update!.Stage);
        Assert.Equal("0.2.0", viewModel.Update.VersionText);

        // The card is made only when it is first shown.
        Assert.Null(TryFind<UpdateCard>(window, "UpdateCard"));
    }

    [AvaloniaFact]
    public async Task OnlyTheIconBelow900px()
    {
        var (window, viewModel) = await ShowOfferAsync(width: 800);
        var button = Find<Button>(window, "UpdateButton");

        Assert.True(viewModel.IsNarrowWindow);
        Assert.True(button.IsEffectivelyVisible);
        Assert.Contains("narrow", button.Classes);
        Assert.Equal(32, button.Bounds.Width);
        Assert.True(Find<StrokeIcon>(button, "UpdateButtonIcon").IsEffectivelyVisible);
        Assert.False(Find<TextBlock>(button, "UpdateButtonText").IsEffectivelyVisible);

        // Without its text the button says what it is in its tooltip.
        Assert.Equal(Strings.UpdateButtonName, ToolTip.GetTip(button));

        window.Width = 1024;
        Settle(window);
        Assert.True(Find<TextBlock>(button, "UpdateButtonText").IsEffectivelyVisible);
        Assert.True(button.Bounds.Width > 32);
    }

    [AvaloniaFact]
    public async Task AutomationNameIsSet()
    {
        var (window, _) = await ShowOfferAsync();
        var button = Find<Button>(window, "UpdateButton");

        Assert.Equal("Update available", AutomationProperties.GetName(button));
        Assert.Equal(Strings.UpdateButtonName, ToolTip.GetTip(button));

        // It takes its own clicks: the system must not treat it as part of the drag area.
        Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(button));
    }

    [AvaloniaFact]
    public async Task ReachableByKeyboardAndOpensTheCardWithEnter()
    {
        var (window, viewModel) = await ShowOfferAsync();
        var palette = Find<Button>(window, "PaletteButton");
        var button = Find<Button>(window, "UpdateButton");

        // Tab goes from "Search or command" to the button, Enter opens the card.
        palette.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.Same(button, Focused(window));

        Press(window, Key.Enter, PhysicalKey.Enter, None);

        Assert.True(viewModel.Update!.IsCardOpen);
        Assert.True(Find<UpdateCard>(window, "UpdateCard").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData(1280)]
    [InlineData(800)]
    public async Task DoesNotCoverTheCaptionButtons(double width)
    {
        var (window, viewModel) = await ShowOfferAsync(width: width);
        foreach (var name in new[] { "hello_world.pdf", "text-rich.pdf", "outline.pdf", "vector-shapes.pdf" })
        {
            await viewModel.OpenAsync(TestData.Pdf(name));
        }

        Settle(window);
        var tabStrip = Find<TabStrip>(window, "TabStrip");
        var palette = Find<Button>(window, "PaletteButton");
        var button = Find<Button>(window, "UpdateButton");
        var caption = Find<Control>(window, "CaptionButtonsSpace");
        double Left(Control control) => control.TranslatePoint(default, window)!.Value.X;
        double Right(Control control) => control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;

        // From the left: tab strip, "Search or command", "Update", the 140 px of the caption buttons; nothing overlaps.
        Assert.True(Right(tabStrip) <= Left(palette) + 0.5, $"tab strip ends at {Right(tabStrip)}, palette starts at {Left(palette)}");
        Assert.True(Right(palette) <= Left(button) + 0.5, $"palette ends at {Right(palette)}, update starts at {Left(button)}");
        Assert.True(Right(button) <= Left(caption) + 0.5, $"update ends at {Right(button)}, caption buttons start at {Left(caption)}");
        Assert.True(Right(button) <= width - 140, $"update ends at {Right(button)}");
        Assert.Equal(140, caption.Bounds.Width);

        // Every tab and the drag area stay inside the strip, which got the button's width less.
        foreach (var tab in tabStrip.GetVisualDescendants().OfType<TabView>().Where(tab => tab.IsEffectivelyVisible))
        {
            Assert.True(Right(tab) <= Right(tabStrip) + 0.5, $"a tab ends at {Right(tab)}, beyond the strip ({Right(tabStrip)})");
        }

        Assert.True(Find<Control>(window, "DragArea").Bounds.Width >= 48);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SitsLeftOfTheCaptionButtonsOnWindowsAndAtTheEndOfTheTabRowOnLinux(bool windowsTitleBar)
    {
        var (window, _) = await ShowOfferAsync(windowsTitleBar: windowsTitleBar);
        var palette = Find<Button>(window, "PaletteButton");
        var button = Find<Button>(window, "UpdateButton");
        var left = button.TranslatePoint(default, window)!.Value.X;
        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
        var paletteRight = palette.TranslatePoint(new Point(palette.Bounds.Width, 0), window)!.Value.X;

        // Right after "Search or command"; then the caption buttons (Windows) or the end of the row (Linux), 8 px away.
        Assert.True(left >= paletteRight, $"update starts at {left}, palette ends at {paletteRight}");
        Assert.Equal(windowsTitleBar ? window.Width - 140 - 8 : window.Width - 8, right, 0.5);
    }

    private async Task<(MainWindow Window, MainViewModel ViewModel)> ShowOfferAsync(double width = 1024, bool windowsTitleBar = true)
    {
        Source.Offer("0.2.0");
        var (window, viewModel, _) = await ShowAppAsync(width: width, windowsTitleBar: windowsTitleBar);
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.Update?.ShowButton == true);
        Settle(window);
        return (window, viewModel);
    }
}
