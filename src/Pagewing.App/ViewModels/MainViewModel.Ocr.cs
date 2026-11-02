using System.Windows.Input;
using Pagewing.App.Services;
using Pagewing.Core.Ocr;
using Pagewing.Core.Settings;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The card of the text recognition in a window (milestone 15): a tab's command asks for it (<see cref="TabViewModel.OcrRequested"/>), the window makes the
/// card and shows it as the overlay <see cref="OverlayKind.Ocr"/>. The card is the job: while a run goes on no other overlay replaces it, it ends with the
/// tab that runs (a tab that closes cancels its run), and closing the window cancels it. Esc stops a run (the question after a stop answers "Discard").
/// Nothing of it is made, read or loaded before the first use: no language file is listed, no library is loaded, no engine is made.
/// </summary>
public sealed partial class MainViewModel
{
    private OcrViewModel? _ocr;
    private TabViewModel? _ocrTab;
    private bool _ocrFeaturesEnabled = true;
    private bool _ocrForced;
    private RelayCommand? _cancelOcrCommand;

    /// <summary>Gets the card that is shown (its view model), or <see langword="null"/>; a new one is made for each use.</summary>
    public OcrViewModel? Ocr => _ocr;

    /// <summary>
    /// Gets or sets whether the tabs of the window offer text recognition (the hint, the command; <see cref="TabViewModel.OcrFeaturesEnabled"/>); on by
    /// default. A measurement run switches it off, so the start time budgets measure the open and the first sharp page without it.
    /// </summary>
    internal bool OcrFeaturesEnabled
    {
        get => _ocrFeaturesEnabled;
        set
        {
            if (_ocrFeaturesEnabled == value)
            {
                return;
            }

            _ocrFeaturesEnabled = value;
            foreach (var tab in Tabs)
            {
                tab.OcrFeaturesEnabled = value;
            }
        }
    }

    /// <summary>Gets or sets what makes the engine factory for a folder of language files (Tesseract when <see langword="null"/>; tests put a fake here).</summary>
    internal Func<string, IOcrEngineFactory>? OcrEngineFactory { get; set; }

    /// <summary>Gets or sets what answers the dialog "Add language file" (the dialog of the window's file picker when <see langword="null"/>; tests answer without a dialog).</summary>
    internal Func<Task<string?>>? LanguageFilePicker { get; set; }

    /// <summary>Gets or sets what makes the store of the language files (the folders of the program and of the user when <see langword="null"/>; tests use a folder of their own, never the real one).</summary>
    internal Func<TessdataStore>? TessdataStoreProvider { get; set; }

    /// <summary>The command of Esc for the card: stops a run, answers the question after a stop with "Discard", or closes the card.</summary>
    public ICommand CancelOcrCommand => _cancelOcrCommand ??= new RelayCommand(() => _ocr?.HandleEscape(), () => Overlay == OverlayKind.Ocr && _ocr is not null);

    private bool IsOcrRunning => _ocr is { IsBusy: true };

    /// <summary>Shows the card for <paramref name="tab"/> when the command may run now.</summary>
    internal void ShowOcr(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (_disposed || Tabs.IndexOf(tab) < 0 || !tab.CanRunOcr || IsOcrRunning)
        {
            return;
        }

        // A card that is shown and does not run goes first (its view model is disposed, the view takes the new one).
        if (Overlay == OverlayKind.Ocr)
        {
            HideOverlay();
        }

        var store = TessdataStoreProvider?.Invoke() ?? new TessdataStore(UserDataPaths.TessdataDirectory(), TessdataStore.ShippedDirectoryFor(AppContext.BaseDirectory));
        Func<Task<string?>>? picker = LanguageFilePicker ?? (_picker is ILanguageFilePicker languages ? languages.PickLanguageFileAsync : null);
        var card = new OcrViewModel(tab, store, Settings, folder => OcrEngineFactory?.Invoke(folder) ?? new TesseractEngineFactory(folder), picker);
        card.Closed += HideOverlay;
        _ocr = card;
        _ocrTab = tab;
        ShowOverlay(OverlayKind.Ocr);
        _cancelOcrCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>The card of <paramref name="tab"/> ends (the tab closes): a run is cancelled and nothing of it is written.</summary>
    private void CancelOcrOf(TabViewModel tab)
    {
        if (_ocr is null || !ReferenceEquals(_ocrTab, tab))
        {
            return;
        }

        // The overlay is hidden for good, whatever the run does.
        _ocrForced = true;
        try
        {
            if (Overlay == OverlayKind.Ocr)
            {
                HideOverlay();
            }
        }
        finally
        {
            _ocrForced = false;
        }

        DisposeOcr();
    }

    /// <summary>Lets go of the card and cancels its run (the overlay went, the tab went, the window closes).</summary>
    private void DisposeOcr()
    {
        var card = _ocr;
        _ocr = null;
        _ocrTab = null;
        card?.Dispose();
        _cancelOcrCommand?.RaiseCanExecuteChanged();
    }
}
