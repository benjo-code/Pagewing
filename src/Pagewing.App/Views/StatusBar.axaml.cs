using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

public sealed partial class StatusBar : UserControl
{
    /// <summary>
    /// Below this width the status bar drops the word "Page", the name of the layout and the label "Document info"
    /// (design system section 6, narrow window).
    /// </summary>
    public const double NarrowWidth = 760;

    /// <summary>The smallest zoom step in the zoom menu (25 %).</summary>
    public const double MenuMinZoom = 0.25;

    /// <summary>The largest zoom step in the zoom menu (400 %).</summary>
    public const double MenuMaxZoom = 4.0;

    /// <summary>Tolerance when comparing the zoom with a step of the menu (as in <see cref="ZoomSteps"/>).</summary>
    private const double StepEpsilon = 1e-9;

    /// <summary>Tabular figures for the zoom values and shortcuts of the menu (design system 5.2).</summary>
    private static readonly FontFeatureCollection TabularFigures = new() { FontFeature.Parse("+tnum") };

    private readonly ObservableCollection<Control> _zoomMenuEntries = [];
    private TabViewModel? _tab;

    public StatusBar()
    {
        InitializeComponent();

        // An entry that was not committed with Enter does not stay in the field.
        PageField.LostFocus += OnPageFieldLostFocus;

        if (ZoomMenuButton.Flyout is MenuFlyout menu)
        {
            menu.ItemsSource = _zoomMenuEntries;
            menu.Opening += OnZoomMenuOpening;

            // The entries hold the commands of their tab: a closed menu drops them.
            menu.Closed += (_, _) => _zoomMenuEntries.Clear();
        }

        InitializeLayoutMenu();
        UpdateLayoutButton();
        InitializeDocumentInfo();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        // The popup belongs to the tab it was opened for.
        CloseDocumentInfo();
        if (_tab is not null)
        {
            _tab.PageFieldFocusRequested -= OnPageFieldFocusRequested;
            _tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _tab = DataContext as TabViewModel;
        if (_tab is not null)
        {
            _tab.PageFieldFocusRequested += OnPageFieldFocusRequested;
            _tab.PropertyChanged += OnTabPropertyChanged;
        }

        UpdateLayoutButton();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var wide = e.NewSize.Width >= NarrowWidth;
        PageLabel.IsVisible = wide;
        LayoutText.IsVisible = wide;
        DocumentInfoText.IsVisible = wide;
    }

    private void OnPageFieldLostFocus(object? sender, RoutedEventArgs e) => (DataContext as TabViewModel)?.ResetPageField();

    /// <summary>"Go to page" (Ctrl+G): the page field takes the keys with its content selected, so typing replaces it.</summary>
    private void OnPageFieldFocusRequested()
    {
        // A keyboard command: the focus ring shows where the keys went (design system 5.3).
        PageField.Focus(NavigationMethod.Tab);
        PageField.SelectAll();
    }

    /// <summary>
    /// Fills the zoom menu when it opens (nothing of it is created before): the steps from <see cref="MenuMinZoom"/>
    /// to <see cref="MenuMaxZoom"/>, a separator, then page width, whole page and actual size with their shortcuts.
    /// The active choice has a check mark on the left (a state never shown by colour alone, design system 7.10): the
    /// mode, or in the free zoom the step that equals the zoom.
    /// </summary>
    private void OnZoomMenuOpening(object? sender, EventArgs e)
    {
        MenuKeyboardFocus.EnsureRegistered();
        _zoomMenuEntries.Clear();
        if (DataContext is not TabViewModel tab)
        {
            return;
        }

        for (var zoom = MenuMinZoom; zoom <= MenuMaxZoom + StepEpsilon; zoom = ZoomSteps.Next(zoom))
        {
            var text = zoom.ToString("P0", Strings.Culture);
            var entry = new MenuItem
            {
                Header = text,
                Command = tab.SetZoomCommand,
                CommandParameter = zoom,
                Icon = tab.ZoomMode == ZoomMode.Free && Math.Abs(tab.Zoom - zoom) < StepEpsilon ? Checkmark() : null,
                FontFeatures = TabularFigures,
            };
            AutomationProperties.SetName(entry, text);
            _zoomMenuEntries.Add(entry);
        }

        _zoomMenuEntries.Add(new Separator());
        _zoomMenuEntries.Add(ModeEntry(tab, ZoomMode.FitWidth, Strings.ZoomFitWidth, Strings.ZoomFitWidthShortcut));
        _zoomMenuEntries.Add(ModeEntry(tab, ZoomMode.FitPage, Strings.ZoomFitPage, Strings.ZoomFitPageShortcut));
        _zoomMenuEntries.Add(ModeEntry(tab, ZoomMode.ActualSize, Strings.ZoomActualSize, Strings.ZoomActualSizeShortcut));
    }

    /// <summary>An entry for a zoom mode: its name, and its shortcut in text.secondary at the right edge.</summary>
    private MenuItem ModeEntry(TabViewModel tab, ZoomMode mode, string name, string shortcut)
    {
        // The shortcut is a text of the UI language ("Strg+2"), which the gesture column of a menu entry cannot show.
        var shortcutText = new TextBlock { Text = shortcut, Margin = new Thickness(24, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        shortcutText.Bind(TextBlock.ForegroundProperty, shortcutText.GetResourceObservable("Pw.Brush.Text.Secondary"));
        Grid.SetColumn(shortcutText, 1);
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { new TextBlock { Text = name }, shortcutText },
        };

        var entry = new MenuItem
        {
            Header = header,
            Command = tab.SetZoomModeCommand,
            CommandParameter = mode,
            Icon = tab.ZoomMode == mode ? Checkmark() : null,
            FontFeatures = TabularFigures,
        };
        AutomationProperties.SetName(entry, name);
        return entry;
    }

    private StrokeIcon Checkmark() => new() { Data = this.FindResource("Pw.Icon.Check") as Geometry };
}
