using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Text recognition of the tab (milestone 15): the hint "This looks like a scan. Make it searchable?" and the lock that holds while a run goes on. The
/// run itself and its card belong to the window (<see cref="MainViewModel.Ocr"/>); the tab only asks for it (<see cref="OcrRequested"/>) and is
/// locked meanwhile: reading, scrolling and the search stay possible, editing, saving, printing and the other modes wait (<see cref="IsEditingFrozen"/>,
/// <see cref="SaveBlockedReason"/>). Nothing here is made or read before the first sharp page of a document, and a measurement run never asks
/// (<see cref="OcrFeaturesEnabled"/>); the hint never starts a run.
/// </summary>
public sealed partial class TabViewModel
{
    private bool _isOcrRunning;
    private bool _showsOcrHint;
    private bool _ocrHintScanned;
    private bool _ocrHintDismissed;
    private RelayCommand? _ocrCommand;
    private RelayCommand? _dismissOcrHintCommand;
    private RelayCommand? _neverAskOcrCommand;

    /// <summary>
    /// Gets or sets whether the text recognition features are on: the hint, the command and the card. On by default; a measurement run switches it
    /// off (<see cref="MainViewModel.OcrFeaturesEnabled"/>), so the start time budgets do not include it.
    /// </summary>
    internal bool OcrFeaturesEnabled
    {
        get => _ocrFeaturesEnabled;
        set
        {
            if (_ocrFeaturesEnabled != value)
            {
                _ocrFeaturesEnabled = value;
                _ocrCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(ShowsOcrHint));
            }
        }
    }

    private bool _ocrFeaturesEnabled = true;

    /// <summary>Gets or sets what tells whether the OCR library is there (set by the window): without it the hint "This looks like a scan" is not offered.</summary>
    internal Func<bool> OcrLibraryAvailable { get; set; } = static () => true;

    /// <summary>
    /// Gets whether a text recognition run of this tab goes on (set by the window while its card runs). The tab is locked meanwhile: no markup, no
    /// form value, no organizing or redacting, no undo, redo, save or print (<see cref="IsEditingFrozen"/>, <see cref="SaveBlockedReason"/>).
    /// </summary>
    public bool IsOcrRunning
    {
        get => _isOcrRunning;
        internal set
        {
            if (SetProperty(ref _isOcrRunning, value))
            {
                if (value)
                {
                    DiscardPendingSignature();
                    ShowsOcrHint = false;
                }

                OnPropertyChanged(nameof(IsEditingFrozen));
                OnPropertyChanged(nameof(OcrProgressText));
                OnPropertyChanged(nameof(SaveBlockedReason));
                OnPropertyChanged(nameof(PageOutputBlockedReason));
                OnPropertyChanged(nameof(CanRunPageOutput));
                RaiseSaveCanExecuteChanged();
                RaiseAnnotateCanExecuteChanged();
                RaiseStepCanExecuteChanged();
                _ocrCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets the text of the status bar while a run goes on ("Recognizing text: 3 of 12 pages"), empty otherwise.</summary>
    public string OcrProgressText
    {
        get => _isOcrRunning ? _ocrProgressText : string.Empty;
        internal set
        {
            if (_ocrProgressText != value)
            {
                _ocrProgressText = value;
                OnPropertyChanged();
            }
        }
    }

    private string _ocrProgressText = string.Empty;

    /// <summary>Gets whether the hint "This looks like a scan" is shown over the document (once per tab and document, never on its own accord a run).</summary>
    public bool ShowsOcrHint
    {
        get => _showsOcrHint && _ocrFeaturesEnabled && !_isOcrRunning && !_ocrHintDismissed;
        private set
        {
            if (_showsOcrHint != value)
            {
                _showsOcrHint = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Gets whether a text recognition run is among the unsaved changes (the warning before saving a signed document names the added text).</summary>
    internal bool HasOcrChanges => _editor?.HasOcrChanges == true;

    /// <summary>Gets why the command cannot run now, or <see langword="null"/>: a mode is on, a run, a save or a print goes on.</summary>
    public string? OcrBlockedReason =>
        _isOcrRunning ? Strings.OcrBlockedRunning
        : _isAnnotating || _isFilling || _isOrganizing || _isRedacting ? Strings.OcrBlockedMode
        : _isSaving || _isPrinting || _isAskedAbout || _isEditingFrozen ? Strings.OcrBlockedBusy
        : null;

    /// <summary>Gets whether the card may be asked for now: an open document after its first sharp page, and nothing that locks it.</summary>
    public bool CanRunOcr =>
        _ocrFeaturesEnabled && !_disposed && IsReady && _document is not null && _hasShownSharpPage && OcrBlockedReason is null;

    /// <summary>The command "Recognize text (OCR) …" (catalog <c>ocr.run</c>): asks the window for the card.</summary>
    public ICommand OcrCommand => _ocrCommand ??= new RelayCommand(() => OcrRequested?.Invoke(), () => CanRunOcr);

    /// <summary>"Not now" of the hint: closes it for this document in this tab (nothing is written to disk).</summary>
    public ICommand DismissOcrHintCommand => _dismissOcrHintCommand ??= new RelayCommand(DismissOcrHint);

    /// <summary>"Don't ask for this file again" of the hint: closes it for this document in this tab (the memory is the tab's, nothing is written to disk).</summary>
    public ICommand NeverAskOcrCommand => _neverAskOcrCommand ??= new RelayCommand(DismissOcrHint);

    /// <summary>Raised by <see cref="OcrCommand"/> and the hint's button: the user wants the card.</summary>
    internal event Action? OcrRequested;

    private void DismissOcrHint()
    {
        _ocrHintDismissed = true;
        OnPropertyChanged(nameof(ShowsOcrHint));
    }

    /// <summary>Raises the changes of everything the state of the tab decides for the command (called with the lock changes).</summary>
    internal void RaiseOcrCanExecuteChanged()
    {
        _ocrCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(OcrBlockedReason));
    }

    /// <summary>Forgets the hint of the old document (a new document, or none).</summary>
    private void ResetOcrHint()
    {
        _ocrHintScanned = false;
        _ocrHintDismissed = false;
        ShowsOcrHint = false;
    }

    /// <summary>
    /// Looks, once per document and after its first sharp page, whether it looks like a scan: the first pages (up to five, at least one but at
    /// most a fifth of the document) have no text worth the name and an image over most of the page. Short background jobs, never in a measurement
    /// run, never twice; the hint follows when they all do.
    /// </summary>
    internal async Task EnsureOcrHintAsync()
    {
        var document = _document;
        if (!_ocrFeaturesEnabled || _ocrHintScanned || document is null || !_hasShownSharpPage || _disposed || !OcrLibraryAvailable())
        {
            return;
        }

        _ocrHintScanned = true;
        var count = Math.Clamp((int)Math.Ceiling(document.PageCount * 0.2), 1, 5);
        try
        {
            for (var page = 0; page < count; page++)
            {
                var info = await document.GetOcrPageInfoAsync(page).ConfigureAwait(true);
                if (info.TextCharacters >= OcrPlanner.TextThreshold || info.HasOwnLayer || info.ImageCoverage <= 0.8)
                {
                    return;
                }

                if (_disposed || !ReferenceEquals(_document, document))
                {
                    return;
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or ObjectDisposedException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // A page that cannot be read says nothing about a scan: no hint.
            return;
        }

        if (!_disposed && ReferenceEquals(_document, document))
        {
            ShowsOcrHint = true;
        }
    }
}
