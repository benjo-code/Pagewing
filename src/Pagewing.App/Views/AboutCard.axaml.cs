using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The about card (<see cref="OverlayKind.About"/>): version, licence and, on request, the third-party notices. The
/// window shows it with <see cref="Show"/> and hides it with <see cref="Hide"/>; the close button and Esc ask the
/// window's view model to hide the overlay.
/// </summary>
public sealed partial class AboutCard : UserControl
{
    private ScrollViewer? _scroller;

    public AboutCard()
    {
        InitializeComponent();

        // Key bindings of the window are matched from the focused element upwards before any key event: with the focus
        // on the card, Esc closes it and the arrow and page keys scroll the notices (the toggle has no use for them).
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(Close) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageUp), Command = new RelayCommand(() => Scroller?.PageUp()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.PageDown), Command = new RelayCommand(() => Scroller?.PageDown()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Up), Command = new RelayCommand(() => Scroller?.LineUp()) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Down), Command = new RelayCommand(() => Scroller?.LineDown()) });
        AboutCloseButton.Click += (_, _) => Close();
        NoticesToggle.IsCheckedChanged += (_, _) => OnNoticesToggled();
    }

    private ScrollViewer? Scroller => _scroller ??= NoticesList.FindNameScope() is { } scope ? scope.Find<ScrollViewer>("NoticesScroller") : null;

    /// <summary>Shows the card with the list of notices closed, and puts the focus on its first control.</summary>
    internal void Show()
    {
        NoticesToggle.IsChecked = false;
        IsVisible = true;

        // After the layout of this change, when the button can take the focus.
        Dispatcher.UIThread.Post(FocusFirst, DispatcherPriority.Loaded);
    }

    /// <summary>Hides the card.</summary>
    internal void Hide() => IsVisible = false;

    /// <summary>Puts the keys on the notices button while the card is shown.</summary>
    internal void FocusFirst()
    {
        if (IsVisible && NoticesToggle.IsEffectivelyVisible)
        {
            NoticesToggle.Focus();
        }
    }

    private void Close() => (TopLevel.GetTopLevel(this)?.DataContext as MainViewModel)?.HideOverlay();

    private void OnNoticesToggled()
    {
        var open = NoticesToggle.IsChecked == true;
        NoticesBox.IsVisible = open;
        NoticesChevronDown.IsVisible = !open;
        NoticesChevronUp.IsVisible = open;

        // Read the text and make the rows only now, and only once.
        if (open && NoticesList.ItemsSource is null && DataContext is AboutViewModel about)
        {
            NoticesList.ItemsSource = about.Notices;
        }
    }
}
