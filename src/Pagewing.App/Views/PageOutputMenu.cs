using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;

namespace Pagewing.App.Views;

/// <summary>
/// The menu "Extract, split, export" of the pages panel (milestone 13, spec 5.3): three entries on the commands of a tab, on the
/// flyout of a button. The panel (outside the mode) and the tools of the mode each have one button; this keeps the entries and
/// their help texts (why an entry cannot run) in step with the tab. Made with the panel, never in the startup path.
/// </summary>
internal sealed class PageOutputMenu
{
    private readonly Button _button;
    private TabViewModel? _tab;
    private MenuItem? _extractItem;

    /// <summary>Creates the menu on the flyout of <paramref name="button"/> (a <see cref="MenuFlyout"/>).</summary>
    public PageOutputMenu(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        _button = button;
    }

    /// <summary>Puts the entries on the commands of <paramref name="tab"/> (none: the menu is empty) and follows its reasons.</summary>
    public void Attach(TabViewModel? tab)
    {
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
        }

        _tab = tab;
        _extractItem = null;
        if (_button.Flyout is not MenuFlyout flyout)
        {
            return;
        }

        if (tab is null)
        {
            flyout.ItemsSource = null;
            return;
        }

        tab.PropertyChanged += OnTabPropertyChanged;
        _extractItem = new MenuItem { Name = "ExtractItem", Header = Strings.CommandExtractPages, Command = tab.ExtractSelectionCommand };
        flyout.ItemsSource = new Control[]
        {
            _extractItem,
            new MenuItem { Name = "SplitItem", Header = Strings.CommandSplitDocument, Command = tab.SplitCommand },
            new MenuItem { Name = "ExportImagesItem", Header = Strings.CommandExportImages, Command = tab.ExportImagesCommand },
        };
        UpdateReasons();
    }

    /// <summary>Stops following the tab (the panel or the tools went).</summary>
    public void Detach()
    {
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabPropertyChanged;
            _tab = null;
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.ExtractUnavailableReason) or nameof(TabViewModel.PageOutputBlockedReason))
        {
            UpdateReasons();
        }
    }

    // The reasons are the help text of the button and of the entry for the selection, so screen readers and the tooltip say
    // why nothing can be chosen.
    private void UpdateReasons()
    {
        var blocked = _tab?.PageOutputBlockedReason;
        AutomationProperties.SetHelpText(_button, blocked);
        ToolTip.SetTip(_button, blocked is null ? Strings.PageOutputMenu : $"{Strings.PageOutputMenu}: {blocked}");
        if (_extractItem is not null)
        {
            AutomationProperties.SetHelpText(_extractItem, _tab?.ExtractUnavailableReason);
        }
    }
}
