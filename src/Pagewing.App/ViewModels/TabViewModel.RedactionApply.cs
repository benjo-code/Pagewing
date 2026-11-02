using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Redaction;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Applying the marks and the preview (milestone 14, task 13). "Apply" asks the window (<see cref="RedactionApplyRequested"/>), which
/// holds the flow: the card, the work in the background, the check and the report. The preview shows the marks as filled areas of
/// the colour they will have, and the numbers of what redacting removes (the analysis of the plan, on a working copy of the file,
/// never on the document that is shown); it changes nothing and renders nothing but the layer of the app (the plan) — in
/// particular it never renders the working copy. Nothing of it is made before the mode is on.
/// </summary>
public sealed partial class TabViewModel
{
    private RelayCommand? _applyRedactionsCommand;
    private RelayCommand? _toggleRedactionPreviewCommand;
    private bool _isRedactionPreview;
    private string? _redactionPreviewText;
    private CancellationTokenSource? _previewCancellation;
    private RedactionPlan? _previewPlan;

    /// <summary>
    /// The command of the button "Apply …" of the toolbar (and of the catalog's <c>redact.apply</c>): asks the window to redact
    /// (<see cref="RedactionApplyRequested"/>). It runs only with a mark waiting, and not while the tab saves, prints, is asked about or
    /// locked by an update. What it does is the window's: it asks what to do about unsaved changes, shows what redacting does and
    /// writes.
    /// </summary>
    public ICommand ApplyRedactionsCommand => _applyRedactionsCommand ??= new RelayCommand(() => RedactionApplyRequested?.Invoke(), () => CanApplyRedactions);

    /// <summary>Raised by <see cref="ApplyRedactionsCommand"/>: the user wants the marks applied.</summary>
    internal event Action? RedactionApplyRequested;

    /// <summary>
    /// Whether the window may start applying the marks now (the flow of <see cref="MainViewModel.ApplyRedactionsAsync"/>): a mark
    /// waits, a document is open, and no save, no print job, no card about the tab and no update lock. The button and Ctrl+S ask the
    /// same; <see cref="SaveBlockedReason"/> says why while a print job runs.
    /// </summary>
    internal bool CanApplyRedactions =>
        !_disposed && IsReady && _document is not null && HasPendingRedactions && !_isSaving && !_isPrinting && !_isAskedAbout && !IsEditingFrozen;

    /// <summary>Gets whether the preview is on: the marks are shown as filled areas and the toolbar shows the numbers of what is removed.</summary>
    public bool IsRedactionPreview => _isRedactionPreview;

    /// <summary>
    /// Gets the line of the preview ("Removes: 38 characters, 1 image; 1 text object as a whole", or what stands in the way), or
    /// <see langword="null"/> while the preview is off.
    /// </summary>
    public string? RedactionPreviewText => _redactionPreviewText;

    /// <summary>The command of the button "Preview": shows or hides the preview. Runs only in the mode.</summary>
    public ICommand ToggleRedactionPreviewCommand => _toggleRedactionPreviewCommand ??= new RelayCommand(ToggleRedactionPreview, () => !_disposed && _isRedacting);

    /// <summary>
    /// Gets or sets how the numbers of the preview are worked out for a plan (the window sets it: <see cref="MainViewModel.RedactionAnalyzer"/>
    /// for this tab). Without it the preview shows the areas and no numbers.
    /// </summary>
    internal Func<RedactionPlan, CancellationToken, Task<RedactionAnalysis>>? RedactionPreviewAnalyzer { get; set; }

    /// <summary>Gets or sets how long the plan has to rest before the preview works out its numbers again (a drag makes many plans).</summary>
    internal TimeSpan RedactionPreviewDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    private void ToggleRedactionPreview()
    {
        if (_isRedactionPreview)
        {
            EndRedactionPreview();
            return;
        }

        if (!_isRedacting)
        {
            return;
        }

        _isRedactionPreview = true;
        OnPropertyChanged(nameof(IsRedactionPreview));
        SchedulePreview();
    }

    /// <summary>The mode ends or the plan is dropped: the preview goes, and a run that is working is stopped.</summary>
    private void EndRedactionPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        _previewPlan = null;
        if (_isRedactionPreview)
        {
            _isRedactionPreview = false;
            OnPropertyChanged(nameof(IsRedactionPreview));
        }

        SetPreviewText(null);
    }

    // The plan changed while the preview is on: its numbers are worked out again once the plan has rested.
    private void SchedulePreviewIfPlanChanged()
    {
        if (_isRedactionPreview && !ReferenceEquals(_previewPlan, _redactionHistory?.Current))
        {
            SchedulePreview();
        }
    }

    private void SchedulePreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation = null;
        if (!_isRedactionPreview || _redactionHistory is not { } history || _disposed)
        {
            return;
        }

        var plan = history.Current;
        _previewPlan = plan;
        if (plan.IsEmpty)
        {
            SetPreviewText(Strings.RedactPreviewEmpty);
            return;
        }

        if (RedactionPreviewAnalyzer is not { } analyzer)
        {
            SetPreviewText(null);
            return;
        }

        SetPreviewText(Strings.RedactPreviewWorking);
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        _ = RunPreviewAsync(plan, analyzer, cancellation);
    }

    private async Task RunPreviewAsync(RedactionPlan plan, Func<RedactionPlan, CancellationToken, Task<RedactionAnalysis>> analyzer, CancellationTokenSource cancellation)
    {
        string text;
        try
        {
            if (RedactionPreviewDelay > TimeSpan.Zero)
            {
                await Task.Delay(RedactionPreviewDelay, cancellation.Token);
            }

            var analysis = await analyzer(plan, cancellation.Token);
            text = RedactionApplyInfo.PreviewLine(analysis);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // The file went, changed or cannot be read: the numbers are missing, the preview of the areas stays (nothing was changed).
            text = Strings.RedactPreviewFailed;
        }

        if (!cancellation.IsCancellationRequested && !_disposed && ReferenceEquals(_previewCancellation, cancellation))
        {
            SetPreviewText(text);
        }
    }

    private void SetPreviewText(string? text)
    {
        if (!string.Equals(_redactionPreviewText, text, StringComparison.Ordinal))
        {
            _redactionPreviewText = text;
            OnPropertyChanged(nameof(RedactionPreviewText));
        }
    }

    private void RaiseRedactionApplyCanExecuteChanged()
    {
        _applyRedactionsCommand?.RaiseCanExecuteChanged();
        _toggleRedactionPreviewCommand?.RaiseCanExecuteChanged();
    }
}
