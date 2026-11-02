using System.Globalization;
using Pagewing.App.Resources;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The offer in the card of a file that cannot be redacted because of some pages (milestone 14.1, spec 4.3): each of these pages can
/// be turned into an image instead, by the user's choice alone (<see cref="RasterRescuePage.IsChosen"/> is never preselected), at one
/// resolution for all of them (150, 200 or 300 dots per inch; 200 first). The card is the question; "Continue" puts the choice into
/// the plan and the analysis runs again, so what the pages lose is told in numbers before anything is applied. UI thread only.
/// </summary>
public sealed class RedactionRescueOffer : ViewModelBase
{
    private int _dpi = RedactionLimits.DefaultRasterDpi;

    internal RedactionRescueOffer(IEnumerable<(int Page, double WidthPoints, double HeightPoints)> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        Pages = [.. pages.Select(page => new RasterRescuePage(this, page.Page, page.WidthPoints, page.HeightPoints))];
    }

    /// <summary>Gets the pages that can be turned into images, in page order.</summary>
    public IReadOnlyList<RasterRescuePage> Pages { get; }

    /// <summary>Gets the pages the user chose, 0-based and ascending.</summary>
    public IReadOnlyList<int> ChosenPages => [.. Pages.Where(static page => page.IsChosen).Select(static page => page.Page)];

    /// <summary>Gets whether at least one page is chosen: "Continue" waits for that.</summary>
    public bool CanContinue => Pages.Any(static page => page.IsChosen);

    /// <summary>Gets or sets the resolution in dots per inch; 150, 200 or 300.</summary>
    public int Dpi
    {
        get => _dpi;
        set
        {
            if ((value is 150 or 200 or 300) && SetProperty(ref _dpi, value))
            {
                OnPropertyChanged(nameof(IsDpi150));
                OnPropertyChanged(nameof(IsDpi200));
                OnPropertyChanged(nameof(IsDpi300));
                foreach (var page in Pages)
                {
                    page.RaiseEstimate();
                }

                Changed?.Invoke();
            }
        }
    }

    /// <summary>Gets or sets whether the resolution is 150 dpi.</summary>
    public bool IsDpi150
    {
        get => _dpi == 150;
        set => Choose(value, 150);
    }

    /// <summary>Gets or sets whether the resolution is 200 dpi.</summary>
    public bool IsDpi200
    {
        get => _dpi == 200;
        set => Choose(value, 200);
    }

    /// <summary>Gets or sets whether the resolution is 300 dpi.</summary>
    public bool IsDpi300
    {
        get => _dpi == 300;
        set => Choose(value, 300);
    }

    /// <summary>Raised when the choice of a page or of the resolution changed (the card asks its buttons again).</summary>
    internal event Action? Changed;

    internal void OnChosenChanged()
    {
        OnPropertyChanged(nameof(CanContinue));
        Changed?.Invoke();
    }

    private void Choose(bool selected, int dpi)
    {
        if (selected)
        {
            Dpi = dpi;
        }
    }
}

/// <summary>One page of a <see cref="RedactionRescueOffer"/> with its check box. UI thread only.</summary>
public sealed class RasterRescuePage : ViewModelBase
{
    private readonly RedactionRescueOffer _offer;
    private readonly double _widthPoints;
    private readonly double _heightPoints;
    private bool _isChosen;

    internal RasterRescuePage(RedactionRescueOffer offer, int page, double widthPoints, double heightPoints)
    {
        _offer = offer;
        Page = page;
        _widthPoints = widthPoints;
        _heightPoints = heightPoints;
    }

    /// <summary>Gets the 0-based page.</summary>
    public int Page { get; }

    /// <summary>Gets the page as the user counts it, "Page 3".</summary>
    public string PageText => Strings.RedactPageHeaderOf(Page + 1);

    /// <summary>Gets the name of the check box for screen readers, with the page.</summary>
    public string ChoiceAutomationName => string.Format(Strings.Culture, System.Text.CompositeFormat.Parse(Strings.RedactRescueChoiceName), (Page + 1).ToString(CultureInfo.InvariantCulture));

    /// <summary>Gets or sets whether the user chose to turn this page into an image. Never set at first.</summary>
    public bool IsChosen
    {
        get => _isChosen;
        set
        {
            if (SetProperty(ref _isChosen, value))
            {
                _offer.OnChosenChanged();
            }
        }
    }

    /// <summary>Gets what the choice costs at the resolution that is set: the pixels and the size the file grows by at most.</summary>
    public string EstimateText => RedactionApplyInfo.RasterEstimate(_widthPoints, _heightPoints, _offer.Dpi) ?? string.Empty;

    internal void RaiseEstimate() => OnPropertyChanged(nameof(EstimateText));
}
