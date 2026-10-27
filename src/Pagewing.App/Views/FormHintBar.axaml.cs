using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Forms;

namespace Pagewing.App.Views;

/// <summary>
/// The hint bar of a form over the document area (milestone 11): one message of the active tab at a time, with a close
/// button. In this order: the notice of a refused value (<see cref="TabViewModel.FormNotice"/>); the offer of the mode with
/// the first hint (<see cref="TabViewModel.OffersFill"/>, <see cref="TabViewModel.PendingHints"/>); the next hint. Its
/// <c>DataContext</c> is the active tab; the window shows the bar only while <see cref="HasMessage"/> holds.
/// </summary>
public sealed partial class FormHintBar : UserControl
{
    private TabViewModel? _tab;

    public FormHintBar()
    {
        InitializeComponent();
        CloseHintButton.Click += OnCloseClick;
    }

    /// <summary>What the bar shows.</summary>
    private enum MessageKind
    {
        /// <summary>Nothing: the bar is hidden.</summary>
        None,

        /// <summary>The reason of a refused value.</summary>
        Notice,

        /// <summary>"Fill in form" with the first hint, or with the sentence that the document has fields.</summary>
        Offer,

        /// <summary>A hint alone.</summary>
        Hint,
    }

    /// <summary>Whether the tab has something for the bar to show.</summary>
    /// <param name="tab">The tab.</param>
    /// <returns>Whether the bar is shown for it.</returns>
    internal static bool HasMessage(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        return KindOf(tab) != MessageKind.None;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _tab = DataContext as TabViewModel;
        if (_tab is not null)
        {
            _tab.PropertyChanged += OnTabPropertyChanged;
        }

        UpdateMessage();
    }

    private static MessageKind KindOf(TabViewModel tab) =>
        tab.FormNotice is not null ? MessageKind.Notice
        : tab.OffersFill ? MessageKind.Offer
        : tab.PendingHints != FormHint.None ? MessageKind.Hint
        : MessageKind.None;

    /// <summary>The first of the hints (the lowest flag), or <see cref="FormHint.None"/>.</summary>
    private static FormHint FirstHint(FormHint hints) => hints == FormHint.None ? FormHint.None : (FormHint)((int)hints & -(int)hints);

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.FormNotice) or nameof(TabViewModel.OffersFill) or nameof(TabViewModel.PendingHints))
        {
            UpdateMessage();
        }
    }

    /// <summary>Shows the message of the tab: its symbol (warning or information), its text, and "Fill in form" with the offer.</summary>
    private void UpdateMessage()
    {
        var kind = _tab is { } tab ? KindOf(tab) : MessageKind.None;
        var hint = _tab is { } shown ? FirstHint(shown.PendingHints) : FormHint.None;
        var text = kind switch
        {
            MessageKind.Notice => _tab!.FormNotice!,
            MessageKind.Offer => hint == FormHint.None ? Strings.FormHintOffer : TabViewModel.HintTextOf(hint),
            MessageKind.Hint => TabViewModel.HintTextOf(hint),
            _ => string.Empty,
        };

        HintText.Text = text;
        AutomationProperties.SetName(this, text);
        NoticeIcon.IsVisible = kind == MessageKind.Notice;
        HintIcon.IsVisible = kind != MessageKind.Notice;
        FillButton.IsVisible = kind == MessageKind.Offer;
    }

    /// <summary>Closes the message shown: the notice; the offer and the hint shown with it; or the hint.</summary>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (_tab is not { } tab)
        {
            return;
        }

        var hint = FirstHint(tab.PendingHints);
        switch (KindOf(tab))
        {
            case MessageKind.Notice:
                tab.DismissFormNoticeCommand.Execute(null);
                break;
            case MessageKind.Offer:
                tab.CloseFillOfferCommand.Execute(null);
                if (hint != FormHint.None)
                {
                    tab.DismissHintCommand.Execute(hint);
                }

                break;
            case MessageKind.Hint:
                tab.DismissHintCommand.Execute(hint);
                break;
        }
    }
}
