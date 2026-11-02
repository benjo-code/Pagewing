using System.Windows.Input;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Pages that become images (milestone 14.1, spec 4.3): the free choice of a page in the plan (the command "Page as image" of the
/// panel, the palette and the entry of the page list) and the choice the card of a rejected page puts into the plan. A raster page
/// is a step of the history like a mark, so undo takes it back; a plan of raster pages alone counts as an unsaved change
/// (<see cref="HasPendingRedactions"/>). Nothing of it is made before the mode is on.
/// </summary>
public sealed partial class TabViewModel
{
    private static readonly int[] RasterDpiChoices = [150, 200, 300];

    private RelayCommand? _toggleRasterPageCommand;
    private RelayCommand<int>? _setRasterDpiCommand;

    /// <summary>
    /// The command "Page as image" (catalog <c>redact.rasterPage</c>, the button of the panel): turns the pages it concerns into images in
    /// the plan, or takes them out again when they all are. It concerns the pages of the selected marks, else the page in view. One
    /// step of the history. Runs only in the mode.
    /// </summary>
    public ICommand ToggleRasterPageCommand => _toggleRasterPageCommand ??= new RelayCommand(ToggleRasterPage, () => CanEditRedactions && RasterTargets().Count > 0);

    /// <summary>Sets the resolution of the pages that become images (parameter: 72 to 300 dots per inch); a step of the history.</summary>
    public ICommand SetRasterDpiCommand => _setRasterDpiCommand ??= new RelayCommand<int>(
        dpi => StageRedaction(plan => plan.WithRasterDpi(dpi), null),
        dpi => CanEditRedactions && dpi is >= RedactionLimits.MinRasterDpi and <= RedactionLimits.MaxRasterDpi);

    /// <summary>Gets whether the plan turns at least one page into an image.</summary>
    public bool HasRasterPages => _redactionHistory?.Current.RasterPages.Count > 0;

    /// <summary>Gets the resolution of the pages that become images, in dots per inch.</summary>
    public int RasterDpi => _redactionHistory?.Current.RasterDpi ?? RedactionLimits.DefaultRasterDpi;

    /// <summary>Gets or sets whether the resolution is 150 dpi (the radio choice of the panel; setting it to <see langword="true"/> is a step).</summary>
    public bool IsRasterDpi150
    {
        get => RasterDpi == 150;
        set => ChooseRasterDpi(value, 150);
    }

    /// <summary>Gets or sets whether the resolution is 200 dpi, the default.</summary>
    public bool IsRasterDpi200
    {
        get => RasterDpi == 200;
        set => ChooseRasterDpi(value, 200);
    }

    /// <summary>Gets or sets whether the resolution is 300 dpi.</summary>
    public bool IsRasterDpi300
    {
        get => RasterDpi == 300;
        set => ChooseRasterDpi(value, 300);
    }

    /// <summary>Gets whether the pages <see cref="ToggleRasterPageCommand"/> concerns all become images (the state of its button).</summary>
    public bool RasterTargetsAreImages
    {
        get
        {
            if (_redactionHistory?.Current is not { } plan)
            {
                return false;
            }

            var targets = RasterTargets();
            return targets.Count > 0 && targets.All(plan.RasterPages.Contains);
        }
    }

    /// <summary>
    /// Puts the choice of the card of a rejected page into the plan: each page becomes an image, one step of the history for each, the
    /// first together with the resolution. Called by the apply flow, which holds the save lock of the tab (so it works while the tab
    /// saves and while a card asks about it, but never while an update has locked editing).
    /// </summary>
    /// <param name="pages">The 0-based pages.</param>
    /// <param name="dpi">The resolution in dots per inch.</param>
    /// <returns>Whether every page is in the plan now (a plan that is full takes none of the rest and says so).</returns>
    internal bool StageRasterChoice(IReadOnlyList<int> pages, int dpi)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var first = true;
        foreach (var page in pages)
        {
            var withDpi = first;
            first = false;
            if (!StageRedaction(plan => withDpi ? plan.WithRasterDpi(dpi).WithRasterPage(page, true) : plan.WithRasterPage(page, true), null, duringApply: true)
                && _redactionHistory?.Current.RasterPages.Contains(page) != true)
            {
                return false;
            }
        }

        return true;
    }

    private void ChooseRasterDpi(bool selected, int dpi)
    {
        if (selected)
        {
            SetRasterDpiCommand.Execute(dpi);
        }
    }

    private void ToggleRasterPage()
    {
        var pages = RasterTargets();
        if (pages.Count == 0 || _redactionHistory?.Current is not { } current)
        {
            return;
        }

        var on = !pages.All(current.RasterPages.Contains);
        StageRedaction(plan => pages.Aggregate(plan, (acc, page) => acc.WithRasterPage(page, on)), null);
    }

    // The pages of the selected marks (the selection of the panel), else the page in view.
    private List<int> RasterTargets()
    {
        if (_selectedMarks.Count > 0 && _redactionHistory?.Current is { } plan)
        {
            var pages = plan.Marks.Where(mark => _selectedMarks.Contains(mark.Id)).Select(static mark => mark.Page).Distinct().Order().ToList();
            if (pages.Count > 0)
            {
                return pages;
            }
        }

        return _currentPage >= 1 && _currentPage <= _pageCount ? [_currentPage - 1] : [];
    }

    private void RaiseRasterStateChanged()
    {
        OnPropertyChanged(nameof(HasRasterPages));
        OnPropertyChanged(nameof(RasterDpi));
        OnPropertyChanged(nameof(IsRasterDpi150));
        OnPropertyChanged(nameof(IsRasterDpi200));
        OnPropertyChanged(nameof(IsRasterDpi300));
        OnPropertyChanged(nameof(RasterTargetsAreImages));
        _toggleRasterPageCommand?.RaiseCanExecuteChanged();
        _setRasterDpiCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Gets the resolutions the panel and the card offer.</summary>
    internal static IReadOnlyList<int> OfferedRasterDpi => RasterDpiChoices;
}
