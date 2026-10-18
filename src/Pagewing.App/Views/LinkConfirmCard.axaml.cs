using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The card that asks before an address of the document is opened (<see cref="MainViewModel.PendingLink"/>); its
/// buttons are the commands of the window's view model. The window shows it with <see cref="Show"/> and hides it with
/// <see cref="Hide"/>.
/// </summary>
public sealed partial class LinkConfirmCard : UserControl
{
    // Does nothing: a key the card takes away from the window while it is up.
    private static readonly RelayCommand Ignore = new(static () => { });

    // The address shown whose layout has not been seen yet (Show); its lock starts with the first layout.
    private PendingLink? _awaitingLayout;

    public LinkConfirmCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus
        // on the card, these keys of the document (page turning, history, search, page field) do nothing behind it.
        foreach (var gesture in new[]
        {
            new KeyGesture(Key.PageUp),
            new KeyGesture(Key.PageDown),
            new KeyGesture(Key.Left, KeyModifiers.Alt),
            new KeyGesture(Key.Right, KeyModifiers.Alt),
            new KeyGesture(Key.F3),
            new KeyGesture(Key.F3, KeyModifiers.Shift),
            new KeyGesture(Key.F, KeyModifiers.Control),
            new KeyGesture(Key.G, KeyModifiers.Control),
        })
        {
            KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = Ignore });
        }

        // The second (or third) press of a multiple click never reaches the buttons: it belongs to a double click that
        // began on the document, where the card was not yet. A press on "Open" while it is locked never reaches it
        // either, so its release after the lock cannot open the address; nor do Enter and Space on it.
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Whether "Open" is locked: the view model has not armed the address shown yet.</summary>
    private bool IsOpenLocked => DataContext is not MainViewModel { IsLinkArmed: true };

    private bool IsOnOpenButton(object? source) =>
        source is Visual visual && (ReferenceEquals(visual, LinkOpenButton) || LinkOpenButton.IsVisualAncestorOf(visual));

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount > 1 || (IsOnOpenButton(e.Source) && IsOpenLocked))
        {
            e.Handled = true;
        }
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && IsOnOpenButton(e.Source) && IsOpenLocked)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Shows <paramref name="link"/>: the full address with the host bold where it really stands
    /// (<see cref="PendingLink.HostRange"/>), the warning line if there is one, and the focus on "Cancel". The lock of
    /// "Open" starts when the card has been laid out (<see cref="MainViewModel.OnLinkCardShown"/>).
    /// </summary>
    internal void Show(PendingLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (_awaitingLayout is null)
        {
            LayoutUpdated += OnLayoutUpdated;
        }

        _awaitingLayout = link;
        var url = link.Link.Url;
        var inlines = new InlineCollection();
        if (link.HostRange is { } host)
        {
            inlines.Add(new Run(url[..host.Start]));
            inlines.Add(new Run(url.Substring(host.Start, host.Length)) { FontWeight = FontWeight.Bold });
            inlines.Add(new Run(url[(host.Start + host.Length)..]));
        }
        else
        {
            inlines.Add(new Run(url));
        }

        LinkAddress.Inlines = inlines;

        // A live region: a screen reader reads the address when the card appears, not only "Cancel" with the focus.
        AutomationProperties.SetName(LinkAddress, url);
        LinkAddressScroller.Offset = default;
        var warning = link.WarningText;
        LinkWarningText.Text = warning;
        LinkWarning.IsVisible = warning is not null;
        IsVisible = true;

        // After the layout of this change, when the button can take the focus.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (LinkCancelButton.IsEffectivelyVisible)
                {
                    LinkCancelButton.Focus();
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>The first layout after <see cref="Show"/> with the card on screen: the lock of "Open" starts now.</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_awaitingLayout is not { } link || !IsEffectivelyVisible || LinkOpenButton.Bounds.Width <= 0)
        {
            return;
        }

        StopAwaitingLayout();
        (DataContext as MainViewModel)?.OnLinkCardShown(link);
    }

    private void StopAwaitingLayout()
    {
        if (_awaitingLayout is not null)
        {
            _awaitingLayout = null;
            LayoutUpdated -= OnLayoutUpdated;
        }
    }

    /// <summary>Hides the card and forgets the address.</summary>
    internal void Hide()
    {
        StopAwaitingLayout();
        IsVisible = false;
        AutomationProperties.SetName(LinkAddress, null);
        LinkAddress.Inlines = null;
        LinkWarningText.Text = null;
    }
}
