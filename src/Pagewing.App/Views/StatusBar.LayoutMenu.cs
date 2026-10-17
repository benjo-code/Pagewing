using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// The layout button of the status bar: icon and name of the layout of the tab, and the layout menu with the four
/// layouts, the current one with a check mark.
/// </summary>
public sealed partial class StatusBar
{
    /// <summary>The layouts in the order of the menu.</summary>
    private static readonly PageLayoutMode[] LayoutModes =
        [PageLayoutMode.SinglePage, PageLayoutMode.Continuous, PageLayoutMode.TwoPages, PageLayoutMode.TwoPagesWithCover];

    private readonly ObservableCollection<Control> _layoutMenuEntries = [];

    /// <summary>The name of a layout in the UI language.</summary>
    internal static string LayoutName(PageLayoutMode mode) => mode switch
    {
        PageLayoutMode.SinglePage => Strings.LayoutSingle,
        PageLayoutMode.TwoPages => Strings.LayoutTwoPages,
        PageLayoutMode.TwoPagesWithCover => Strings.LayoutTwoPagesCover,
        _ => Strings.LayoutContinuous,
    };

    /// <summary>The key of the icon of a layout (design system 5.5): file, layout-rows, layout-columns, book.</summary>
    private static string LayoutIconKey(PageLayoutMode mode) => mode switch
    {
        PageLayoutMode.SinglePage => "Pw.Icon.File",
        PageLayoutMode.TwoPages => "Pw.Icon.LayoutColumns",
        PageLayoutMode.TwoPagesWithCover => "Pw.Icon.Book",
        _ => "Pw.Icon.LayoutRows",
    };

    /// <summary>Gives the empty layout menu its entry list; the entries are created when it opens.</summary>
    private void InitializeLayoutMenu()
    {
        if (LayoutButton.Flyout is MenuFlyout menu)
        {
            menu.ItemsSource = _layoutMenuEntries;
            menu.Opening += OnLayoutMenuOpening;

            // The entries hold the command of their tab: a closed menu drops them.
            menu.Closed += (_, _) => _layoutMenuEntries.Clear();
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.LayoutMode))
        {
            UpdateLayoutButton();
        }

        OnTabChangedForDocumentInfo(e);
    }

    /// <summary>Shows the layout of the tab on the button: its icon, its name, and the tooltip that names it.</summary>
    private void UpdateLayoutButton()
    {
        var mode = _tab?.LayoutMode ?? PageLayoutMode.Continuous;
        var name = LayoutName(mode);
        LayoutIcon.Data = FindIcon(LayoutIconKey(mode));
        LayoutText.Text = name;
        var tip = string.Format(Strings.Culture, CompositeFormat.Parse(Strings.LayoutTooltip), name);
        ToolTip.SetTip(LayoutButton, tip);
        AutomationProperties.SetName(LayoutButton, tip);
    }

    /// <summary>Fills the layout menu when it opens: the four layouts, the current one with a check mark on the left.</summary>
    private void OnLayoutMenuOpening(object? sender, EventArgs e)
    {
        _layoutMenuEntries.Clear();
        if (DataContext is not TabViewModel tab)
        {
            return;
        }

        foreach (var mode in LayoutModes)
        {
            var name = LayoutName(mode);
            var entry = new MenuItem
            {
                Header = name,
                Command = tab.SetLayoutModeCommand,
                CommandParameter = mode,
                Icon = tab.LayoutMode == mode ? Checkmark() : null,
            };
            AutomationProperties.SetName(entry, name);
            _layoutMenuEntries.Add(entry);
        }
    }

    /// <summary>An icon of Styles/Icons.axaml; also before the status bar is in a window (the application has them).</summary>
    private Geometry? FindIcon(string key) =>
        (this.TryFindResource(key, out var icon) ? icon : Application.Current?.FindResource(key)) as Geometry;
}
