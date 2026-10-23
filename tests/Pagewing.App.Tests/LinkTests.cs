using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class LinkTests : IDisposable
{
    private const string DocsUrl = "https://example.org/docs";
    private const string TextUrl = "https://example.org/pagewing";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    // The clock of the arming delay of "Open"; it only moves when a test moves it.
    private readonly ManualClock _clock = new();

    public LinkTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task ClickingAnInternalLinkJumpsWithoutAsking()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");

        // Zoomed in far enough that the target point (250 pt below the top of page 2) can reach the top of the viewport.
        tab.Zoom = 3;
        Settle(window);
        var text = await TextAsync(window, tab, 0);
        var jump = JumpLink(text);
        var target = Assert.IsType<PageTarget>(jump.Target);
        Assert.Equal(1, target.PageIndex);
        var top = Assert.NotNull(target.Top);
        Assert.Equal(250, top, 1);
        ScrollToShow(view, 0, jump.Rects[0]);
        Settle(window);

        Click(window, ScreenPoint(view, window, 0, Centre(jump.Rects[0])));

        Assert.Equal(2, tab.CurrentPage);
        Assert.Null(viewModel.PendingLink);
        Assert.Empty(launcher.Calls);
        var layout = view.Layout!;
        var expected = layout.GetPageRect(1).Y + (top * DocumentLayout.DipsPerPoint * layout.Zoom) - DocumentLayout.Margin;
        Assert.Equal(expected, view.ScrollViewer.Offset.Y, 1);
        Assert.True(tab.CanGoBack);
        Assert.False(tab.CanGoForward);
    }

    [AvaloniaFact]
    public async Task BackAndForwardReturnToTheViewBeforeTheJump()
    {
        var (window, viewModel, view, _) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        tab.Zoom = 3;
        Settle(window);
        var text = await TextAsync(window, tab, 0);
        var jump = JumpLink(text);
        ScrollToShow(view, 0, jump.Rects[0]);
        Settle(window);
        var before = view.ScrollViewer.Offset;
        Assert.False(tab.CanGoBack);

        Click(window, ScreenPoint(view, window, 0, Centre(jump.Rects[0])));
        Assert.Equal(2, tab.CurrentPage);
        var after = view.ScrollViewer.Offset;
        Assert.NotEqual(before.Y, after.Y, 1);

        // Alt+Left: page 1 at the old position.
        view.Focus();
        Press(window, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(before.Y, view.ScrollViewer.Offset.Y, 1);
        Assert.Equal(before.X, view.ScrollViewer.Offset.X, 1);
        Assert.False(tab.CanGoBack);
        Assert.True(tab.CanGoForward);

        // Alt+Right: page 2 where the jump went.
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        Assert.Equal(2, tab.CurrentPage);
        Assert.Equal(after.Y, view.ScrollViewer.Offset.Y, 1);
        Assert.True(tab.CanGoBack);
        Assert.False(tab.CanGoForward);
    }

    [AvaloniaFact]
    public async Task HistoryKeepsTheLastFiftyJumpsAndANewJumpClearsForward()
    {
        var (window, viewModel, view, _) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var area = new[] { new PageRect(0, 0, 10, 10) };

        for (var i = 0; i < 60; i++)
        {
            tab.FollowLink(new PageLink(area, new PageTarget((i + 1) % 2, null)));
            Settle(window);
        }

        var backs = 0;
        while (tab.CanGoBack)
        {
            tab.BackCommand.Execute(null);
            Settle(window);
            backs++;
            Assert.True(backs <= 60, "the history does not end");
        }

        Assert.Equal(TabViewModel.MaxHistory, backs);
        Assert.Equal(50, TabViewModel.MaxHistory);
        Assert.True(tab.CanGoForward);
        Assert.False(tab.BackCommand.CanExecute(null));
        Assert.True(tab.ForwardCommand.CanExecute(null));

        // Two steps forward, then a new jump: the rest of the forward list goes.
        tab.ForwardCommand.Execute(null);
        tab.ForwardCommand.Execute(null);
        Settle(window);
        Assert.True(tab.CanGoForward);
        tab.FollowLink(new PageLink(area, new PageTarget(1, null)));
        Settle(window);
        Assert.False(tab.CanGoForward);
        Assert.True(tab.CanGoBack);

        // A target that is not a page of the document does nothing: it is not remembered and keeps the forward list.
        tab.BackCommand.Execute(null);
        Settle(window);
        var page = tab.CurrentPage;
        tab.FollowLink(new PageLink(area, new PageTarget(2, null)));
        tab.FollowLink(new PageLink(area, new PageTarget(-1, null)));
        Settle(window);
        Assert.True(tab.CanGoForward);
        Assert.Equal(page, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task AnInternalLinkInTheSinglePageLayoutShowsThePage()
    {
        var (window, viewModel, view, _) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.SinglePage);
        Settle(window);
        var text = await TextAsync(window, tab, 0);
        var jump = JumpLink(text);

        Click(window, ScreenPoint(view, window, 0, Centre(jump.Rects[0])));

        var layout = view.Layout!;
        Assert.True(layout.IsShown(1));
        Assert.False(layout.IsShown(0));
        Assert.Equal(2, tab.CurrentPage);

        // As near the target point as the content lets it be.
        var target = layout.GetPageRect(1).Y + (250 * DocumentLayout.DipsPerPoint * layout.Zoom) - DocumentLayout.Margin;
        var max = Math.Max(0, layout.ContentHeight - view.ScrollViewer.Viewport.Height);
        Assert.Equal(Math.Clamp(target, 0, max), view.ScrollViewer.Offset.Y, 1);

        tab.BackCommand.Execute(null);
        Settle(window);
        Assert.True(layout.IsShown(0));
        Assert.Equal(1, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task AnExternalLinkOpensOnlyAfterConfirmation()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);

        // Nothing of the card exists before a link asks for it.
        Assert.Null(FindOrNull<LinkConfirmCard>(window, "LinkConfirmCard"));

        Click(window, ScreenPoint(view, window, 0, Centre(WebLink(text, DocsUrl).Rects[0])));

        Assert.Empty(launcher.Calls);
        var pending = Assert.IsType<PendingLink>(viewModel.PendingLink);
        Assert.Equal(DocsUrl, pending.Link.Url);
        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal(DocsUrl, AddressOf(card));
        Assert.Equal("example.org", BoldPartOf(card));
        Assert.Equal(Strings.LinkConfirmTitle, Find<TextBlock>(card, "LinkTitle").Text);
        Assert.Equal(Strings.LinkConfirmText, Find<TextBlock>(card, "LinkText").Text);
        Assert.False(Find<Control>(card, "LinkWarning").IsVisible);

        // The focus is on "Cancel", the default.
        await WaitUntilAsync(window, () => Find<Button>(card, "LinkCancelButton").IsFocused);
        Assert.Equal(Strings.LinkConfirmCancel, Find<Button>(card, "LinkCancelButton").Content);
        Assert.Equal(Strings.LinkConfirmOpen, Find<Button>(card, "LinkOpenButton").Content);

        // "Open": the launcher gets exactly the address shown, once; the card goes.
        _clock.Advance(MainViewModel.LinkArmingDelay);
        ClickButton(window, Find<Button>(card, "LinkOpenButton"));
        await WaitUntilAsync(window, () => launcher.Calls.Count > 0);
        Assert.Equal([DocsUrl], launcher.Calls);
        Assert.Null(viewModel.PendingLink);
        Assert.False(card.IsVisible);

        // The confirmation command again, with nothing pending, opens nothing.
        viewModel.ConfirmLinkCommand.Execute(null);
        Settle(window);
        Assert.Single(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task CancelingOrEscapeNeverOpensTheAddress()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        var point = ScreenPoint(view, window, 0, Centre(WebLink(text, DocsUrl).Rects[0]));

        // Esc.
        Click(window, point);
        Assert.NotNull(viewModel.PendingLink);
        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        await WaitUntilAsync(window, () => Find<Button>(card, "LinkCancelButton").IsFocused);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(viewModel.PendingLink);
        Assert.False(card.IsVisible);

        // Enter on the focused default button. Each click a little apart from the last, so none counts as a double click.
        Click(window, point + new Point(15, 0));
        Assert.NotNull(viewModel.PendingLink);
        await WaitUntilAsync(window, () => Find<Button>(card, "LinkCancelButton").IsFocused);
        Press(window, Key.Enter, PhysicalKey.Enter);
        Assert.Null(viewModel.PendingLink);

        // The "Cancel" button.
        Click(window, point - new Point(15, 0));
        Assert.NotNull(viewModel.PendingLink);
        ClickButton(window, Find<Button>(card, "LinkCancelButton"));
        Assert.Null(viewModel.PendingLink);
        Assert.False(card.IsVisible);

        // Esc while the focus is somewhere else in the window still cancels.
        Click(window, point);
        Assert.NotNull(viewModel.PendingLink);
        window.Focus();
        Settle(window);
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(viewModel.PendingLink);

        for (var i = 0; i < 10; i++)
        {
            Settle(window);
            await Task.Delay(5);
        }

        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task SwitchingTabsOrReplacingTheDocumentDropsAPendingLink()
    {
        var (window, viewModel, view, launcher) = Show();
        var other = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var area = new[] { new PageRect(0, 0, 10, 10) };

        // Another tab.
        tab.FollowLink(new PageLink(area, new UriTarget(DocsUrl)));
        Assert.NotNull(viewModel.PendingLink);
        viewModel.ActivateTabCommand.Execute(other);
        Settle(window);
        Assert.Null(viewModel.PendingLink);
        Assert.False(Find<LinkConfirmCard>(window, "LinkConfirmCard").IsVisible);

        // A link of a tab that is not active asks nothing.
        tab.FollowLink(new PageLink(area, new UriTarget(DocsUrl)));
        Assert.Null(viewModel.PendingLink);

        // Another document in the tab.
        viewModel.ActivateTabCommand.Execute(tab);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document));
        tab.FollowLink(new PageLink(area, new UriTarget(DocsUrl)));
        Assert.NotNull(viewModel.PendingLink);
        var old = tab.Session.Document;
        await tab.OpenAsync(TestData.Pdf("hello_world.pdf"));
        await WaitUntilAsync(window, () => view.Document is not null && !ReferenceEquals(view.Document, old));
        Assert.Null(viewModel.PendingLink);

        // Closing the tab.
        tab.FollowLink(new PageLink(area, new UriTarget(DocsUrl)));
        Assert.NotNull(viewModel.PendingLink);
        viewModel.CloseTabCommand.Execute(tab);
        Settle(window);
        Assert.Null(viewModel.PendingLink);

        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task AddressesFoundInTheTextAreLinksToo()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        var found = WebLink(text, TextUrl);

        Move(window, ScreenPoint(view, window, 0, Centre(found.Rects[0])));
        Assert.Same(view.LinkCursor, view.ScrollViewer.Cursor);
        Click(window, ScreenPoint(view, window, 0, Centre(found.Rects[0])));

        Assert.Equal(TextUrl, viewModel.PendingLink?.Link.Url);
        Assert.Equal(TextUrl, AddressOf(Find<LinkConfirmCard>(window, "LinkConfirmCard")));
        Assert.Empty(launcher.Calls);
        Assert.Null(tab.TextSelection);
    }

    [AvaloniaFact]
    public async Task DangerousTargetsAreNotClickable()
    {
        // In an empty part of page 1: a script and a file, as a PDF may have them.
        var script = new PageLink([new PageRect(180, 360, 240, 390)], new UriTarget("javascript:alert(1)"));
        var file = new PageLink([new PageRect(250, 360, 310, 390)], new UriTarget("file:///C:/Windows/System32/calc.exe"));
        var (window, viewModel, view, launcher) = Show(WithLinks(_ => [script, file]));
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        Assert.Contains(script, text.Links);
        Assert.Contains(file, text.Links);

        foreach (var link in new[] { script, file })
        {
            var point = ScreenPoint(view, window, 0, Centre(link.Rects[0]));
            Move(window, point);
            Assert.Null(view.ScrollViewer.Cursor);
            Click(window, point);
            Assert.Null(view.PressedLink);
            Assert.Null(viewModel.PendingLink);
            Assert.Equal(1, tab.CurrentPage);

            // Followed by hand, too: nothing.
            tab.FollowLink(link);
            Assert.Null(viewModel.PendingLink);
        }

        Assert.Null(FindOrNull<LinkConfirmCard>(window, "LinkConfirmCard"));
        Assert.False(tab.CanGoBack);
        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task DeceptiveAddressesShowAWarning()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var area = new[] { new PageRect(0, 0, 10, 10) };

        tab.FollowLink(new PageLink(area, new UriTarget("https://paypal.com@evil.example/")));
        Settle(window);

        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        Assert.Equal("https://paypal.com@evil.example/", AddressOf(card));
        Assert.Equal("evil.example", BoldPartOf(card));
        var warning = Find<Control>(card, "LinkWarning");
        Assert.True(warning.IsEffectivelyVisible);
        Assert.Equal(
            Format(Strings.LinkWarningUserInfo, "evil.example"),
            Find<TextBlock>(card, "LinkWarningText").Text);
        Assert.Contains(warning.GetVisualDescendants(), visual => visual is Controls.StrokeIcon);

        // Punycode: the host as it reads.
        viewModel.CancelLinkCommand.Execute(null);
        tab.FollowLink(new PageLink(area, new UriTarget("https://xn--bcher-kva.de/")));
        Settle(window);
        Assert.True(warning.IsEffectivelyVisible);
        Assert.Equal(
            Format(Strings.LinkWarningPunycode, "bücher.de"),
            Find<TextBlock>(card, "LinkWarningText").Text);
        Assert.Equal("https://xn--bcher-kva.de/", AddressOf(card));

        // A plain address has no warning.
        viewModel.CancelLinkCommand.Execute(null);
        tab.FollowLink(new PageLink(area, new UriTarget(DocsUrl)));
        Settle(window);
        Assert.False(warning.IsVisible);
        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task ExternalLinkIsOpenedExactlyAsDisplayed()
    {
        const string url = "https://a.example/%22%20--new-window%20%22x";
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");

        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(url)));
        Settle(window);
        Assert.Equal(url, AddressOf(Find<LinkConfirmCard>(window, "LinkConfirmCard")));

        _clock.Advance(MainViewModel.LinkArmingDelay);
        viewModel.ConfirmLinkCommand.Execute(null);
        await WaitUntilAsync(window, () => launcher.Calls.Count > 0);
        var opened = Assert.Single(launcher.Calls);
        Assert.Equal(url, opened, StringComparer.Ordinal);

        // What the system launcher would start: the address unchanged, never a re-serialised form.
        Assert.True(ExternalLink.TryParse(url, out var link));
        var windows = SystemLinkLauncher.CreateStartInfo(link, windows: true, xdgOpenPath: null, currentPath: null)!;
        Assert.Equal(url, windows.FileName, StringComparer.Ordinal);
        Assert.True(windows.UseShellExecute);
        Assert.Empty(windows.Arguments);
        Assert.Empty(windows.ArgumentList);
        const string path = "/usr/local/bin:/usr/bin";
        var xdgOpen = SystemLinkLauncher.FindOnPath(path, ':', "xdg-open", candidate => candidate == Path.Join("/usr/bin", "xdg-open"));
        var linux = SystemLinkLauncher.CreateStartInfo(link, windows: false, xdgOpen, path)!;
        Assert.True(Path.IsPathRooted(linux.FileName), linux.FileName);
        Assert.Equal(Path.Join("/usr/bin", "xdg-open"), linux.FileName);
        Assert.False(linux.UseShellExecute);
        Assert.Empty(linux.Arguments);
        Assert.Equal([url], linux.ArgumentList);
        Assert.Equal(url, linux.ArgumentList[0], StringComparer.Ordinal);
    }

    [Fact]
    public void XdgOpenRunsInAFixedDirectoryWithOnlyRootedPathEntries()
    {
        Assert.True(ExternalLink.TryParse(DocsUrl, out var link));
        var xdgOpen = Path.Join("/usr/bin", "xdg-open");

        // xdg-open calls its helpers (xdg-mime, x-www-browser, ...) by name: they must not come from the current directory.
        foreach (var (path, expected) in new[]
        {
            (".:/usr/bin:", "/usr/bin"),
            ("bin::/usr/bin", "/usr/bin"),
            ("/usr/local/bin:/usr/bin:", "/usr/local/bin:/usr/bin"),
            ("/opt/x::./y:/usr/bin", "/opt/x:/usr/bin"),
            (".:bin:", SystemLinkLauncher.FallbackPath),
            (string.Empty, SystemLinkLauncher.FallbackPath),
        })
        {
            var info = SystemLinkLauncher.CreateStartInfo(link, windows: false, xdgOpen, path)!;
            Assert.Equal("/", info.WorkingDirectory);
            Assert.True(Path.IsPathRooted(info.WorkingDirectory));
            Assert.Equal(expected, info.Environment["PATH"]);
            Assert.All(info.Environment["PATH"]!.Split(':'), entry => Assert.True(entry.Length > 0 && Path.IsPathRooted(entry), entry));
            Assert.Equal([DocsUrl], info.ArgumentList);
            Assert.Equal(xdgOpen, info.FileName);
        }

        // Every other variable is inherited.
        var inherited = SystemLinkLauncher.CreateStartInfo(link, windows: false, xdgOpen, "/usr/bin")!.Environment;
        foreach (System.Collections.DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = (string)variable.Key;
            if (!string.Equals(name, "PATH", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Equal((string?)variable.Value, inherited[name]);
            }
        }
    }

    [Fact]
    public void XdgOpenIsOnlyTakenFromRootedPathEntries()
    {
        static Func<string, bool> Everywhere(List<string> asked) => path =>
        {
            asked.Add(path);
            return true;
        };

        // Empty and relative entries mean the current directory (where a downloaded archive may put an "xdg-open"): skipped.
        foreach (var path in new[] { "::/usr/bin", ".:/usr/bin", "bin:/usr/bin", ":/usr/bin", "./bin:/usr/bin" })
        {
            var asked = new List<string>();
            Assert.Equal(Path.Join("/usr/bin", "xdg-open"), SystemLinkLauncher.FindOnPath(path, ':', "xdg-open", Everywhere(asked)));
            Assert.Equal([Path.Join("/usr/bin", "xdg-open")], asked);
        }

        // The first rooted entry that has it wins.
        Assert.Equal(
            Path.Join("/opt/b", "xdg-open"),
            SystemLinkLauncher.FindOnPath("/opt/a:/opt/b:/usr/bin", ':', "xdg-open", path => path != Path.Join("/opt/a", "xdg-open")));

        // Nothing found, or no PATH: null, and no start info (nothing is started).
        Assert.Null(SystemLinkLauncher.FindOnPath("/opt/a:.:bin", ':', "xdg-open", path => !path.StartsWith("/opt", StringComparison.Ordinal)));
        Assert.Null(SystemLinkLauncher.FindOnPath(string.Empty, ':', "xdg-open", _ => true));
        Assert.Null(SystemLinkLauncher.FindOnPath(null, ':', "xdg-open", _ => true));
        Assert.True(ExternalLink.TryParse(DocsUrl, out var link));
        Assert.Null(SystemLinkLauncher.CreateStartInfo(link, windows: false, xdgOpenPath: null, currentPath: "/usr/bin"));
        Assert.Null(SystemLinkLauncher.CreateStartInfo(link, windows: false, xdgOpenPath: "xdg-open", currentPath: "/usr/bin"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheSystemLauncherSwallowsAFailedStart(bool windows)
    {
        Assert.True(ExternalLink.TryParse(DocsUrl, out var link));
        var started = new List<ProcessStartInfo>();
        foreach (var failure in new Exception[]
        {
            new System.ComponentModel.Win32Exception(2),
            new InvalidOperationException("no file name"),
            new FileNotFoundException("missing"),
            new PlatformNotSupportedException(),
        })
        {
            var launcher = new SystemLinkLauncher(
                info =>
                {
                    started.Add(info);
                    throw failure;
                },
                windows,
                () => ".:/usr/bin",
                path => path == Path.Join("/usr/bin", "xdg-open"));

            // Completes without an exception.
            await launcher.LaunchAsync(link);
        }

        // Each start was really tried, with what the platform gets.
        Assert.Equal(4, started.Count);
        Assert.All(started, info => Assert.Equal(windows ? DocsUrl : Path.Join("/usr/bin", "xdg-open"), info.FileName));
        Assert.All(started, info => Assert.Equal(windows ? [] : [DocsUrl], info.ArgumentList));

        // Without an xdg-open on PATH nothing is started at all.
        var none = new List<ProcessStartInfo>();
        await new SystemLinkLauncher(info => { none.Add(info); return null; }, windows: false, () => ".:bin", _ => true).LaunchAsync(link);
        Assert.Empty(none);
    }

    [AvaloniaFact]
    public async Task OpenIsIgnoredUntilTheCardIsArmed()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(DocsUrl)));
        Settle(window);
        var pending = Assert.IsType<PendingLink>(viewModel.PendingLink);
        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        var open = Find<Button>(card, "LinkOpenButton");
        var clicks = 0;
        open.Click += (_, _) => clicks++;

        // At once: "Open" is disabled and does not take the focus; neither the pointer nor Enter or Space on it nor the
        // command opens anything; the card stays.
        Assert.False(open.IsEffectivelyEnabled);
        Assert.False(open.Focus());
        RaiseClick(window, CentreOf(open, window), clickCount: 1);
        var pointer = RaisePress(window, open, CentreOf(open, window), clickCount: 1);
        RaiseRelease(window, open, pointer, CentreOf(open, window));
        Assert.True(RaiseKeyDown(window, open, Key.Enter).Handled);
        Assert.True(RaiseKeyDown(window, open, Key.Space).Handled);
        Assert.False(open.IsPressed);
        viewModel.ConfirmLinkCommand.Execute(null);
        _clock.Advance(MainViewModel.LinkArmingDelay - TimeSpan.FromMilliseconds(1));
        Settle(window);
        Assert.False(open.IsEffectivelyEnabled);
        Assert.True(RaiseKeyDown(window, open, Key.Enter).Handled);
        viewModel.ConfirmLinkCommand.Execute(null);
        Settle(window);
        Assert.Equal(0, clicks);
        Assert.Empty(launcher.Calls);
        Assert.Same(pending, viewModel.PendingLink);
        Assert.True(card.IsVisible);

        // Once the delay has passed, "Open" is enabled and opens, exactly once.
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Settle(window);
        Assert.True(open.IsEffectivelyEnabled);
        Assert.True(open.Focus());
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);
        await WaitUntilAsync(window, () => launcher.Calls.Count > 0);
        Press(window, Key.Enter, PhysicalKey.Enter);
        viewModel.ConfirmLinkCommand.Execute(null);
        Settle(window);
        Assert.Equal([DocsUrl], launcher.Calls);
        Assert.Null(viewModel.PendingLink);

        // "Cancel" is never delayed.
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(DocsUrl)));
        Settle(window);
        ClickButton(window, Find<Button>(card, "LinkCancelButton"));
        Assert.Null(viewModel.PendingLink);
        Assert.Single(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task APressOnOpenDuringTheLockDoesNotOpenOnRelease()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(DocsUrl)));
        Settle(window);
        var pending = Assert.IsType<PendingLink>(viewModel.PendingLink);
        var open = Find<Button>(Find<LinkConfirmCard>(window, "LinkConfirmCard"), "LinkOpenButton");
        var point = CentreOf(open, window);

        // Pressed 200 ms after the card was drawn, released 250 ms later (when the lock is over): nothing opens.
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        var pointer = RaisePress(window, open, point, clickCount: 1);
        Assert.False(open.IsPressed);
        _clock.Advance(TimeSpan.FromMilliseconds(250));
        Settle(window);
        Assert.True(viewModel.IsLinkArmed);
        Assert.True(open.IsEffectivelyEnabled);
        RaiseRelease(window, open, pointer, point);
        Assert.Empty(launcher.Calls);
        Assert.Same(pending, viewModel.PendingLink);

        // A press after the lock opens on its release, once.
        pointer = RaisePress(window, open, point, clickCount: 1);
        RaiseRelease(window, open, pointer, point);
        await WaitUntilAsync(window, () => launcher.Calls.Count > 0);
        Assert.Equal([DocsUrl], launcher.Calls);
        Assert.Null(viewModel.PendingLink);
    }

    [AvaloniaFact]
    public async Task TheLockCountsFromWhenTheCardIsLaidOut()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");

        // The address arrives, then the UI thread stalls for a second before the card is laid out and drawn.
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(DocsUrl)));
        var pending = Assert.IsType<PendingLink>(viewModel.PendingLink);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Settle(window);
        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        Assert.True(card.IsEffectivelyVisible);

        // The lock begins with the card on screen, not with the arrival of the address.
        Assert.False(viewModel.IsLinkArmed);
        viewModel.ConfirmLinkCommand.Execute(null);
        _clock.Advance(MainViewModel.LinkArmingDelay - TimeSpan.FromMilliseconds(1));
        Settle(window);
        Assert.False(viewModel.IsLinkArmed);
        viewModel.ConfirmLinkCommand.Execute(null);
        Settle(window);
        Assert.Empty(launcher.Calls);
        Assert.Same(pending, viewModel.PendingLink);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Settle(window);
        Assert.True(viewModel.IsLinkArmed);
        ClickButton(window, Find<Button>(card, "LinkOpenButton"));
        await WaitUntilAsync(window, () => launcher.Calls.Count > 0);
        Assert.Equal([DocsUrl], launcher.Calls);
    }

    [AvaloniaFact]
    public async Task OpenLooksDisabledUntilTheCardIsArmed()
    {
        var (window, viewModel, view, _) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");

        // Nothing waits for a lock before an address asks to be opened.
        Assert.Equal(0, _clock.TimersCreated);
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(DocsUrl)));
        Settle(window);
        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        var open = Find<Button>(card, "LinkOpenButton");
        var cancel = Find<Button>(card, "LinkCancelButton");

        // Locked: "Open" is disabled (and looks it), "Cancel" is not; one timer ends the lock.
        Assert.False(open.IsEffectivelyEnabled);
        Assert.Contains(":disabled", open.Classes);
        Assert.False(viewModel.ConfirmLinkCommand.CanExecute(null));
        Assert.True(cancel.IsEffectivelyEnabled);
        Assert.Equal(1, _clock.TimersCreated);
        _clock.Advance(MainViewModel.LinkArmingDelay - TimeSpan.FromMilliseconds(1));
        Settle(window);
        Assert.False(open.IsEffectivelyEnabled);

        // Armed: "Open" becomes enabled by itself, without any input.
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Settle(window);
        Assert.True(open.IsEffectivelyEnabled);
        Assert.DoesNotContain(":disabled", open.Classes);
        Assert.True(viewModel.ConfirmLinkCommand.CanExecute(null));

        // The next address is locked again.
        viewModel.CancelLinkCommand.Execute(null);
        Settle(window);
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(TextUrl)));
        Settle(window);
        Assert.False(open.IsEffectivelyEnabled);
        Assert.False(viewModel.ConfirmLinkCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TheCardIsAnnouncedToScreenReaders()
    {
        var (window, viewModel, view, _) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(DocsUrl)));
        Settle(window);
        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");

        // The card is named by its title; title and address are a polite live region, so the address is read out when
        // the card appears (not only "Cancel", which has the focus).
        Assert.Equal(Strings.LinkConfirmTitle, AutomationProperties.GetName(card));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(Find<TextBlock>(card, "LinkTitle")));
        var address = Find<SelectableTextBlock>(card, "LinkAddress");
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(address));
        Assert.Equal(DocsUrl, AutomationProperties.GetName(address));

        // The next address is announced with its own name.
        viewModel.CancelLinkCommand.Execute(null);
        Settle(window);
        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(TextUrl)));
        Settle(window);
        Assert.Equal(TextUrl, AutomationProperties.GetName(address));
    }

    [AvaloniaFact]
    public async Task ADoubleClickOnTheDocumentNeverReachesOpen()
    {
        // A link that covers the whole page, so whatever is double-clicked brings the card up.
        var everywhere = new PageLink([new PageRect(0, 0, 400, 400)], new UriTarget(DocsUrl));
        var (window, viewModel, view, launcher) = Show(WithLinks(_ => [everywhere]));
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await TextAsync(window, tab, 0);

        // Where "Open" will be: the card shown once and cancelled.
        tab.FollowLink(everywhere);
        Settle(window);
        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        var open = Find<Button>(card, "LinkOpenButton");
        var point = CentreOf(open, window);
        viewModel.CancelLinkCommand.Execute(null);
        Settle(window);
        Assert.False(card.IsVisible);

        // A real double click there: the first click brings the card up, the second lands on "Open" and opens nothing.
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Assert.NotNull(viewModel.PendingLink);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
        Assert.Empty(launcher.Calls);
        Assert.NotNull(viewModel.PendingLink);

        // While locked, "Open" is disabled and the point hits the card around it; once armed, the point is on "Open".
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, card) || hit.GetVisualAncestors().Contains(card), $"{hit} is not on the card");
        _clock.Advance(MainViewModel.LinkArmingDelay);
        Settle(window);
        hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(point));
        Assert.True(ReferenceEquals(hit, open) || hit.GetVisualAncestors().Contains(open), $"{hit} is not the Open button");

        // The second press of a double click never reaches the button, even after the arming delay.
        RaiseClick(window, point, clickCount: 2);
        Assert.Empty(launcher.Calls);
        Assert.NotNull(viewModel.PendingLink);

        // A single click does (so the presses above did reach the card).
        RaiseClick(window, point, clickCount: 1);
        await WaitUntilAsync(window, () => launcher.Calls.Count > 0);
        Assert.Equal([DocsUrl], launcher.Calls);
    }

    [Fact]
    public void LinkAddressesWithUnsafeUnicodeHostsAreShownAsWritten()
    {
        // Symbols are no letters: the host as the address writes it.
        Assert.Equal(Format(Strings.LinkWarningPunycode, "xn--n3h.example"), Pending("https://xn--n3h.example/").WarningText);
        Assert.Equal(Format(Strings.LinkWarningPunycode, "xn--ls8h.la"), Pending("https://xn--ls8h.la/").WarningText);
        Assert.Equal(Format(Strings.LinkWarningPunycode, "bücher.de"), Pending("https://xn--bcher-kva.de/").WarningText);

        // The real host after user information is named in ASCII, so a look-alike cannot hide in it.
        Assert.Equal(Format(Strings.LinkWarningUserInfo, "xn--pypal-4ve.com"), Pending("https://paypal.com@pаypal.com/").WarningText);
        Assert.Null(Pending(DocsUrl).WarningText);

        // The host is emphasised only where it really stands: after the last "@" of the authority, as written.
        Assert.Equal((19, 12), Pending("https://paypal.com@evil.example/").HostRange);
        Assert.Equal((27, 12), Pending("https://evil.example.x.com@evil.example/").HostRange);
        Assert.Equal((8, 11), Pending(DocsUrl).HostRange);
        Assert.Null(Pending("https://EXAMPLE.org/docs").HostRange);
        Assert.Equal((12, 11), Pending("mailto:info@example.org").HostRange);
        Assert.Null(Pending("mailto:info@EXAMPLE.org").HostRange);
        Assert.Equal((8, 9), Pending("https://bücher.de/").HostRange);
    }

    [AvaloniaFact]
    public async Task ReleaseOnAnotherLinkOrOutsideDoesNotFollow()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        var web = ScreenPoint(view, window, 0, Centre(WebLink(text, DocsUrl).Rects[0]));
        var jump = ScreenPoint(view, window, 0, Centre(JumpLink(text).Rects[0]));

        // Pressed on one link, released on the other.
        Drag(window, web, jump);
        Assert.Null(viewModel.PendingLink);
        Assert.Equal(1, tab.CurrentPage);
        Drag(window, jump, web);
        Assert.Null(viewModel.PendingLink);
        Assert.Equal(1, tab.CurrentPage);

        // Each case presses on another spot of the link, so no press counts as the second of a double click.
        // Released away from the link without a move in between.
        var spot = web - new Point(36, 0);
        window.MouseDown(spot, MouseButton.Left);
        window.MouseUp(spot + new Point(0, 40), MouseButton.Left);
        Settle(window);
        Assert.Null(viewModel.PendingLink);

        // Moved away and back: no longer a click.
        spot = web - new Point(18, 0);
        window.MouseDown(spot, MouseButton.Left);
        window.MouseMove(spot + new Point(10, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(spot, RawInputModifiers.LeftMouseButton);
        window.MouseUp(spot, MouseButton.Left);
        Settle(window);
        Assert.Null(viewModel.PendingLink);

        // With Ctrl or Shift, or with the middle or right button: nothing.
        window.MouseDown(web, MouseButton.Left, RawInputModifiers.Control);
        window.MouseUp(web, MouseButton.Left, RawInputModifiers.Control);
        spot = web + new Point(18, 0);
        window.MouseDown(spot, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(spot, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseDown(web, MouseButton.Middle);
        window.MouseUp(web, MouseButton.Middle);
        window.MouseDown(web, MouseButton.Right);
        window.MouseUp(web, MouseButton.Right);
        Settle(window);
        Assert.Null(viewModel.PendingLink);

        // Never on the press itself.
        spot = web + new Point(36, 0);
        window.MouseDown(spot, MouseButton.Left);
        Settle(window);
        Assert.Null(viewModel.PendingLink);
        window.MouseUp(spot, MouseButton.Left);
        Settle(window);
        Assert.Equal(DocsUrl, viewModel.PendingLink?.Link.Url);
        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task ClickingAndDraggingAcrossALinkDoesNotFollowIt()
    {
        var (window, viewModel, view, _) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        var link = WebLink(text, DocsUrl).Rects[0];

        // From before the link across it to after it: a selection, not a click.
        var from = ScreenPoint(view, window, 0, new Point(link.Left - 20, (link.Top + link.Bottom) / 2));
        var to = ScreenPoint(view, window, 0, new Point(link.Right + 20, (link.Top + link.Bottom) / 2));
        Drag(window, from, to);
        Assert.Null(viewModel.PendingLink);

        // From the link across the page.
        Drag(window, ScreenPoint(view, window, 0, Centre(link)), to);
        Assert.Null(viewModel.PendingLink);
        Assert.Equal(1, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task AnExternalClickWhileTheCardIsUpDoesNotOpenAnotherCard()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);

        Click(window, ScreenPoint(view, window, 0, Centre(WebLink(text, DocsUrl).Rects[0])));
        var pending = Assert.IsType<PendingLink>(viewModel.PendingLink);

        // A click on another link behind the card reaches the card, not the document; a link followed otherwise is ignored.
        var other = WebLink(text, TextUrl);
        Click(window, ScreenPoint(view, window, 0, Centre(other.Rects[0])));
        Assert.Same(pending, viewModel.PendingLink);
        tab.FollowLink(other);
        Assert.Same(pending, viewModel.PendingLink);
        Assert.Equal(DocsUrl, AddressOf(Find<LinkConfirmCard>(window, "LinkConfirmCard")));

        // An internal link neither: the document stays where it is while the card is up.
        Click(window, ScreenPoint(view, window, 0, Centre(JumpLink(text).Rects[0])));
        Assert.Equal(1, tab.CurrentPage);

        // Document keys do not act behind the card.
        await WaitUntilAsync(window, () => Find<Button>(Find<LinkConfirmCard>(window, "LinkConfirmCard"), "LinkCancelButton").IsFocused);
        Press(window, Key.PageDown, PhysicalKey.PageDown);
        Assert.Equal(1, tab.CurrentPage);
        Assert.Same(pending, viewModel.PendingLink);
        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task TheCardCoversTheWholeDocumentArea()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        Click(window, ScreenPoint(view, window, 0, Centre(WebLink(text, DocsUrl).Rects[0])));
        var pending = Assert.IsType<PendingLink>(viewModel.PendingLink);
        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        var box = Find<Border>(card, "LinkCardBox");

        // The heading of the page lies outside the card's box: the pointer there meets the card's backdrop, not the document.
        var heading = ScreenPoint(view, window, 0, Centre(text.Chars[3].Box));
        var boxBounds = new Rect(box.TranslatePoint(default, window)!.Value, box.Bounds.Size);
        Assert.False(boxBounds.Contains(heading), $"{heading} lies on the card {boxBounds}");
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(heading));
        Assert.True(ReferenceEquals(hit, card) || hit.GetVisualAncestors().Contains(card), $"{hit} is not part of the card");

        // So a double click there selects nothing, jumps nowhere and asks nothing new.
        window.MouseDown(heading, MouseButton.Left);
        window.MouseUp(heading, MouseButton.Left);
        window.MouseDown(heading, MouseButton.Left);
        window.MouseUp(heading, MouseButton.Left);
        Settle(window);
        Assert.Null(tab.TextSelection);
        Assert.Equal(1, tab.CurrentPage);
        Assert.False(tab.CanGoBack);
        Assert.Same(pending, viewModel.PendingLink);
        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task AVeryLongAddressKeepsTheButtonsOnTheCard()
    {
        var url = "https://example.org/" + new string('a', ExternalLink.MaxLength - "https://example.org/".Length);
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");

        tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget(url)));
        Settle(window);

        var card = Find<LinkConfirmCard>(window, "LinkConfirmCard");
        Assert.Equal(url, AddressOf(card));
        var scroller = Find<ScrollViewer>(card, "LinkAddressScroller");
        Assert.True(scroller.Bounds.Height <= 180.5, $"the address takes {scroller.Bounds.Height}");
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, "the address does not scroll");

        var box = Find<Border>(card, "LinkCardBox");
        var boxBounds = new Rect(box.TranslatePoint(default, window)!.Value, box.Bounds.Size);
        var windowBounds = new Rect(window.ClientSize);
        Assert.True(windowBounds.Contains(boxBounds), $"the card {boxBounds} leaves the window {windowBounds}");
        foreach (var name in new[] { "LinkCancelButton", "LinkOpenButton" })
        {
            var button = Find<Button>(card, name);
            var bounds = new Rect(button.TranslatePoint(default, window)!.Value, button.Bounds.Size);
            Assert.True(button.IsEffectivelyVisible);
            Assert.True(bounds.Height > 0 && boxBounds.Contains(bounds), $"{name} at {bounds} is outside the card {boxBounds}");
        }

        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task TheSecondClickOfADoubleClickFollowsNothing()
    {
        // A link to the top of its own page in an empty part of page 1: the first click follows it and leaves the
        // document where it is, so the second click lands on the same link.
        var link = new PageLink([new PageRect(180, 360, 240, 390)], new PageTarget(0, null));
        var (window, viewModel, view, _) = Show(WithLinks(_ => [link]));
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await TextAsync(window, tab, 0);
        var point = ScreenPoint(view, window, 0, Centre(link.Rects[0]));

        // The click count is given, not left to the timing of the test.
        RaiseClick(window, point, clickCount: 1);
        RaiseClick(window, point, clickCount: 2);
        RaiseClick(window, point, clickCount: 3);

        // Only the first click went into the history.
        var backs = 0;
        while (tab.CanGoBack)
        {
            tab.BackCommand.Execute(null);
            Settle(window);
            backs++;
        }

        Assert.Equal(1, backs);
    }

    [AvaloniaFact]
    public async Task FollowingALinkNeverCallsTheLauncher()
    {
        var (window, viewModel, view, launcher) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var area = new[] { new PageRect(0, 0, 10, 10) };
        var links = new[] { DocsUrl, "mailto:info@example.org", "http://example.org/", "https://paypal.com@evil.example/" };

        foreach (var url in links)
        {
            tab.FollowLink(new PageLink(area, new UriTarget(url)));
            Settle(window);
            Assert.Equal(url, viewModel.PendingLink?.Link.Url);
            tab.FollowLink(new PageLink(area, new UriTarget(url)));
            viewModel.CancelLinkCommand.Execute(null);
            Settle(window);
        }

        tab.FollowLink(new PageLink(area, new PageTarget(1, 10)));
        tab.BackCommand.Execute(null);
        tab.ForwardCommand.Execute(null);
        Settle(window);
        for (var i = 0; i < 10; i++)
        {
            Settle(window);
            await Task.Delay(5);
        }

        Assert.Empty(launcher.Calls);
    }

    [AvaloniaFact]
    public async Task LinkRectsOfARotatedPageAreClickable()
    {
        const string url = "https://example.org/rotated";

        // rotated-page.pdf has no link: one over its word, in the coordinates of the displayed page, as the engine gives them.
        var (window, viewModel, view, launcher) = Show(WithLinks(text =>
        {
            var area = text.Chars.Where(c => !c.IsGenerated).Select(c => c.Box).Aggregate((a, b) => a.Union(b));
            return [new PageLink([area], new UriTarget(url))];
        }));
        var tab = await OpenAsync(window, viewModel, view, "rotated-page.pdf");
        tab.Zoom = 2;
        Settle(window);
        var text = await TextAsync(window, tab, 0);
        var link = WebLink(text, url);
        Assert.True(link.Rects[0].Height > link.Rects[0].Width * 2, $"{link.Rects[0]} does not lie along the rotated text");

        var point = ScreenPoint(view, window, 0, Centre(link.Rects[0]));
        Move(window, point);
        Assert.Same(view.LinkCursor, view.ScrollViewer.Cursor);
        Click(window, point);
        Assert.Equal(url, viewModel.PendingLink?.Link.Url);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public void LinkStringsExistInBothLanguages()
    {
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Open this web address?", Strings.LinkConfirmTitle);
        Assert.Equal("This PDF wants to open a link in your browser. Only continue if you trust the address.", Strings.LinkConfirmText);
        Assert.Equal("Open", Strings.LinkConfirmOpen);
        Assert.Equal("Cancel", Strings.LinkConfirmCancel);
        Assert.Equal("The address contains a user name before the “@”. The real host is {0}.", Strings.LinkWarningUserInfo);
        Assert.Equal("The host uses special characters and is shown as {0}.", Strings.LinkWarningPunycode);

        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Diese Webadresse öffnen?", Strings.LinkConfirmTitle);
        Assert.Equal("Dieses PDF möchte einen Link in Ihrem Browser öffnen. Fahren Sie nur fort, wenn Sie der Adresse vertrauen.", Strings.LinkConfirmText);
        Assert.Equal("Öffnen", Strings.LinkConfirmOpen);
        Assert.Equal("Abbrechen", Strings.LinkConfirmCancel);
        Assert.Equal("Die Adresse enthält vor dem „@“ einen Benutzernamen. Der eigentliche Host ist {0}.", Strings.LinkWarningUserInfo);
        Assert.Equal("Der Host nutzt Sonderzeichen und wird als {0} dargestellt.", Strings.LinkWarningPunycode);
    }

    private static string Format(string format, string argument) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), argument);

    private static PendingLink Pending(string url) =>
        ExternalLink.TryParse(url, out var link) ? new PendingLink(link) : throw new InvalidOperationException($"{url} is not accepted.");

    private static PageLink WebLink(PageText text, string url) =>
        text.Links.First(link => link.Target is UriTarget uri && uri.Url == url);

    private static PageLink JumpLink(PageText text) => text.Links.First(link => link.Target is PageTarget);

    /// <summary>The text of the address on the card, from its runs.</summary>
    private static string AddressOf(LinkConfirmCard card) =>
        string.Concat(Find<SelectableTextBlock>(card, "LinkAddress").Inlines!.OfType<Run>().Select(run => run.Text));

    /// <summary>The emphasised part of the address, or <see langword="null"/>.</summary>
    private static string? BoldPartOf(LinkConfirmCard card) =>
        Find<SelectableTextBlock>(card, "LinkAddress").Inlines!.OfType<Run>().SingleOrDefault(run => run.FontWeight == FontWeight.Bold)?.Text;

    /// <summary>Text sources whose page 1 has extra links in front of its own (what a PDF may have), from what the engine read.</summary>
    private static Func<PdfDocument, PageTextSource> WithLinks(Func<PageText, IEnumerable<PageLink>> extra) =>
        document => new PageTextSource(document, async (page, priority) =>
        {
            var text = await document.GetPageTextAsync(page, priority, CancellationToken.None);
            return page == 0
                ? new PageText(text.PageIndex, text.Width, text.Height, text.Chars, [.. extra(text), .. text.Links])
                : text;
        });

    /// <summary>Scrolls so that a rectangle of a page lies well inside the viewport.</summary>
    private static void ScrollToShow(DocumentView view, int page, PageRect rect)
    {
        var layout = view.Layout!;
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var y = layout.GetPageRect(page).Y + (rect.Top * scale) - 100;
        view.ScrollViewer.Offset = new Vector(0, y);
    }

    /// <summary>Waits until the text of a page is there (read through the session, as the view does after the first sharp page).</summary>
    private static async Task<PageText> TextAsync(Window window, TabViewModel tab, int page)
    {
        var source = tab.Session.EnsurePageTexts() ?? throw new InvalidOperationException("No document.");
        var task = source.GetAsync(page);
        await WaitUntilAsync(window, () => task.IsCompleted);
        return await task;
    }

    private static Point Centre(PageRect box) => new((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);

    /// <summary>The window point of a point on a page (PDF points from the top left of the page as displayed).</summary>
    private static Point ScreenPoint(DocumentView view, Window window, int page, Point pagePoint)
    {
        var layout = view.Layout!;
        var rect = layout.GetPageRect(page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var canvasPoint = new Point(rect.X - view.Canvas.Viewport.X + (pagePoint.X * scale), rect.Y - view.Canvas.Viewport.Y + (pagePoint.Y * scale));
        return view.Canvas.TranslatePoint(canvasPoint, window)!.Value;
    }

    private static void Move(Window window, Point point)
    {
        window.MouseMove(point);
        Settle(window);
    }

    private static void Click(Window window, Point point)
    {
        Settle(window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    /// <summary>The window point at the centre of a control.</summary>
    private static Point CentreOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    /// <summary>
    /// A press and release of the left button at a window point with the given click count, raised on the element the
    /// point hits (so the count does not depend on the timing of the test, as it does with the headless mouse).
    /// </summary>
    private static void RaiseClick(Window window, Point point, int clickCount)
    {
        var target = window.InputHitTest(point) as Avalonia.Interactivity.Interactive ?? throw new InvalidOperationException($"Nothing at {point}.");
        var pointer = RaisePress(window, target, point, clickCount);
        RaiseRelease(window, target, pointer, point);
    }

    /// <summary>A press of the left button at a window point, raised on <paramref name="target"/>; returns its pointer for the release.</summary>
    private static Pointer RaisePress(Window window, Avalonia.Interactivity.Interactive target, Point point, int clickCount)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, window, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, clickCount));
        Settle(window);
        return pointer;
    }

    /// <summary>The release of a press made with <see cref="RaisePress"/>, raised on <paramref name="target"/>.</summary>
    private static void RaiseRelease(Window window, Avalonia.Interactivity.Interactive target, Pointer pointer, Point point)
    {
        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, window, point, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        pointer.Capture(null);
        Settle(window);
    }

    /// <summary>A key going down, raised on <paramref name="target"/> (tunnelling from the window, then bubbling).</summary>
    private static KeyEventArgs RaiseKeyDown(Window window, Avalonia.Interactivity.Interactive target, Key key)
    {
        var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target };
        target.RaiseEvent(args);
        Settle(window);
        return args;
    }

    private static void ClickButton(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        Click(window, point);
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Settle(window);
        window.MouseMove(from + ((to - from) / 2), RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseUp(to, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Settle(window);
    }

    private (Window Window, MainViewModel ViewModel, DocumentView View, RecordingLinkLauncher Launcher) Show(Func<PdfDocument, PageTextSource>? texts = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()) { PageTextsFactory = texts }, new NoFilePicker()) { IsWindowsTitleBar = true };
        var launcher = new RecordingLinkLauncher();
        viewModel.LinkLauncher = launcher;
        viewModel.Clock = _clock;
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"), launcher);
    }

    /// <summary>Opens a file in a new tab, waits until the view shows it and closes the panel the window opens by itself.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
    }

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
                throw new TimeoutException("The view did not reach the expected state.");
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
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    /// <summary>Records the addresses it is asked to open and opens nothing.</summary>
    private sealed class RecordingLinkLauncher : ILinkLauncher
    {
        public List<string> Calls { get; } = [];

        public Task LaunchAsync(ExternalLink link)
        {
            ArgumentNullException.ThrowIfNull(link);
            Calls.Add(link.Url);
            return Task.CompletedTask;
        }
    }

    /// <summary>A clock that moves only when told to; its timers fire (on the calling thread) when it moves past them.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        /// <summary>Gets how many timers were created on this clock.</summary>
        public int TimersCreated { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _ticks += by.Ticks;
            foreach (var timer in _timers.ToArray())
            {
                timer.FireIfDue(_ticks);
            }
        }

        /// <summary>A one-shot timer of the manual clock (a period is not supported).</summary>
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private long? _due;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                clock._timers.Remove(this);
                _due = dueTime == global::System.Threading.Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
                if (_due is not null)
                {
                    clock._timers.Add(this);
                }

                return true;
            }

            public void FireIfDue(long now)
            {
                if (_due is { } due && due <= now)
                {
                    Dispose();
                    callback(state);
                }
            }

            public void Dispose()
            {
                _due = null;
                clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
