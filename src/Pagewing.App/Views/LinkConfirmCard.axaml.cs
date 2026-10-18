using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
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
    }

    /// <summary>
    /// Shows <paramref name="link"/>: the full address with the host bold where it really stands
    /// (<see cref="PendingLink.HostRange"/>), the warning line if there is one, and the focus on "Cancel".
    /// </summary>
    internal void Show(PendingLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
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

    /// <summary>Hides the card and forgets the address.</summary>
    internal void Hide()
    {
        IsVisible = false;
        LinkAddress.Inlines = null;
        LinkWarningText.Text = null;
    }
}
