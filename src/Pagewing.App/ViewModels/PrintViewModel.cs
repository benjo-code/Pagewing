using System.Globalization;
using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The print dialog of the app (<see cref="OverlayKind.Print"/>; Linux, where CUPS has no dialog of its own): printer,
/// pages, copies, collating, page size, both sides and paper orientation. Made when the dialog is shown, one per showing.
/// It lists the printers in the background and asks the chosen one whether it prints on both sides; a late answer after
/// the dialog closed is dropped. "Print" raises <see cref="Confirmed"/> with the choices, "Cancel" raises
/// <see cref="Cancelled"/>. UI thread only.
/// </summary>
public sealed class PrintViewModel : ViewModelBase, IDisposable
{
    private readonly IPrintService _service;
    private readonly int _pageCount;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly RelayCommand _printCommand;
    private CancellationTokenSource? _duplexCheck;
    private IReadOnlyList<SettingsChoice<string>> _printers = [];
    private SettingsChoice<string>? _selectedPrinter;
    private bool _isSearching;
    private string? _searchFailed;
    private PrintPageChoice _pageChoice;
    private string _rangeText = string.Empty;
    private string? _rangeError;
    private string _copiesText;
    private string? _copiesError;
    private bool _collate;
    private SettingsChoice<PrintScaling> _selectedScaling;
    private SettingsChoice<PrintDuplex> _selectedDuplex;
    private SettingsChoice<PrintOrientation> _selectedOrientation;
    private bool _supportsDuplex;
    private bool _closed;

    /// <summary>Creates the view model for a document.</summary>
    /// <param name="service">The print service.</param>
    /// <param name="pageCount">The pages of the document.</param>
    /// <param name="currentPage">The page the user is looking at, 0-based.</param>
    /// <param name="defaults">What the dialog starts with.</param>
    public PrintViewModel(IPrintService service, int pageCount, int currentPage, PrintSettings defaults)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        defaults = defaults.Normalized();
        _service = service;
        _pageCount = pageCount;
        CurrentPageNumber = Math.Clamp(currentPage, 0, pageCount - 1) + 1;
        _copiesText = defaults.Copies.ToString(CultureInfo.InvariantCulture);
        _collate = defaults.Collate;
        _rangeText = CurrentPageNumber.ToString(CultureInfo.InvariantCulture);

        ScalingChoices =
        [
            new(PrintScaling.FitToPaper, Strings.PrintScalingFit),
            new(PrintScaling.ActualSize, Strings.PrintScalingActual),
        ];
        DuplexChoices =
        [
            new(PrintDuplex.Default, Strings.PrintPrinterSetting),
            new(PrintDuplex.Off, Strings.PrintDuplexOff),
            new(PrintDuplex.LongEdge, Strings.PrintDuplexLong),
            new(PrintDuplex.ShortEdge, Strings.PrintDuplexShort),
        ];
        OrientationChoices =
        [
            new(PrintOrientation.Default, Strings.PrintPrinterSetting),
            new(PrintOrientation.Portrait, Strings.PrintPortrait),
            new(PrintOrientation.Landscape, Strings.PrintLandscape),
        ];
        _selectedScaling = ScalingChoices.First(choice => choice.Value == defaults.Scaling);
        _selectedDuplex = DuplexChoices.First(choice => choice.Value == defaults.Duplex);
        _selectedOrientation = OrientationChoices.First(choice => choice.Value == defaults.Orientation);
        _pageChoice = defaults.Pages;
        if (defaults.Pages == PrintPageChoice.Range && defaults.Range.Length > 0)
        {
            _rangeText = defaults.Range;
        }

        DefaultPrinter = defaults.Printer;
        _printCommand = new RelayCommand(Confirm, () => CanPrint);
        PrintCommand = _printCommand;
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke());
        Validate();
    }

    /// <summary>Raised when the user chose "Print"; the ticket carries the choices.</summary>
    public event Action<PrintTicket>? Confirmed;

    /// <summary>Raised when the user closed the dialog without printing.</summary>
    public event Action? Cancelled;

    /// <summary>Gets the number of pages of the document, for the field of the range.</summary>
    public int PageCount => _pageCount;

    /// <summary>Gets the page the user is looking at, 1-based.</summary>
    public int CurrentPageNumber { get; }

    /// <summary>Gets the printer to select when the list arrives (the one used last), or an empty string.</summary>
    public string DefaultPrinter { get; }

    /// <summary>Gets the printers the system offers; empty until the list arrived.</summary>
    public IReadOnlyList<SettingsChoice<string>> Printers
    {
        get => _printers;
        private set
        {
            _printers = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNoPrinters));
        }
    }

    /// <summary>Gets or sets the chosen printer.</summary>
    public SettingsChoice<string>? SelectedPrinter
    {
        get => _selectedPrinter;
        set
        {
            if (SetProperty(ref _selectedPrinter, value))
            {
                SupportsDuplex = false;
                SelectedDuplex = DuplexChoices[0];
                _ = CheckDuplexAsync(value?.Value);
                Changed();
            }
        }
    }

    /// <summary>Gets whether the list of printers is still being fetched.</summary>
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(HasNoPrinters));
            }
        }
    }

    /// <summary>Gets why the list could not be fetched, or <see langword="null"/>.</summary>
    public string? SearchFailed
    {
        get => _searchFailed;
        private set
        {
            if (SetProperty(ref _searchFailed, value))
            {
                OnPropertyChanged(nameof(HasNoPrinters));
            }
        }
    }

    /// <summary>Gets whether the system has no printer (the list arrived and is empty).</summary>
    public bool HasNoPrinters => !IsSearching && SearchFailed is null && Printers.Count == 0;

    /// <summary>Gets which pages to print.</summary>
    public PrintPageChoice PageChoice
    {
        get => _pageChoice;
        private set
        {
            if (SetProperty(ref _pageChoice, value))
            {
                OnPropertyChanged(nameof(AllPages));
                OnPropertyChanged(nameof(CurrentPageSelected));
                OnPropertyChanged(nameof(RangeSelected));
                Validate();
            }
        }
    }

    /// <summary>Gets or sets whether every page is chosen (a radio button; setting it to <see langword="false"/> does nothing).</summary>
    public bool AllPages
    {
        get => _pageChoice == PrintPageChoice.All;
        set
        {
            if (value)
            {
                PageChoice = PrintPageChoice.All;
            }
        }
    }

    /// <summary>Gets or sets whether the current page is chosen.</summary>
    public bool CurrentPageSelected
    {
        get => _pageChoice == PrintPageChoice.CurrentPage;
        set
        {
            if (value)
            {
                PageChoice = PrintPageChoice.CurrentPage;
            }
        }
    }

    /// <summary>Gets or sets whether a range is chosen.</summary>
    public bool RangeSelected
    {
        get => _pageChoice == PrintPageChoice.Range;
        set
        {
            if (value)
            {
                PageChoice = PrintPageChoice.Range;
            }
        }
    }

    /// <summary>Gets or sets the text of the range (<c>1-3,5</c>); typing in it chooses the range.</summary>
    public string RangeText
    {
        get => _rangeText;
        set
        {
            if (SetProperty(ref _rangeText, value ?? string.Empty))
            {
                PageChoice = PrintPageChoice.Range;
                Validate();
            }
        }
    }

    /// <summary>Gets what is wrong with the range, or <see langword="null"/>.</summary>
    public string? RangeError
    {
        get => _rangeError;
        private set => SetProperty(ref _rangeError, value);
    }

    /// <summary>Gets or sets the number of copies as typed.</summary>
    public string CopiesText
    {
        get => _copiesText;
        set
        {
            if (SetProperty(ref _copiesText, value ?? string.Empty))
            {
                Validate();
            }
        }
    }

    /// <summary>Gets what is wrong with the number of copies, or <see langword="null"/>.</summary>
    public string? CopiesError
    {
        get => _copiesError;
        private set => SetProperty(ref _copiesError, value);
    }

    /// <summary>Gets or sets whether copies come out as complete sets.</summary>
    public bool Collate
    {
        get => _collate;
        set => SetProperty(ref _collate, value);
    }

    /// <summary>Gets the ways a page is fitted onto the paper.</summary>
    public IReadOnlyList<SettingsChoice<PrintScaling>> ScalingChoices { get; }

    /// <summary>Gets or sets how a page is fitted onto the paper.</summary>
    public SettingsChoice<PrintScaling> SelectedScaling
    {
        get => _selectedScaling;
        set
        {
            if (value is not null && SetProperty(ref _selectedScaling, value))
            {
                OnPropertyChanged(nameof(ActualSizeSelected));
            }
        }
    }

    /// <summary>Gets whether "actual size" is chosen (the dialog then says that the edges may be cut off).</summary>
    public bool ActualSizeSelected => _selectedScaling.Value == PrintScaling.ActualSize;

    /// <summary>Gets the choices for printing on both sides.</summary>
    public IReadOnlyList<SettingsChoice<PrintDuplex>> DuplexChoices { get; }

    /// <summary>Gets or sets the choice for printing on both sides.</summary>
    public SettingsChoice<PrintDuplex> SelectedDuplex
    {
        get => _selectedDuplex;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedDuplex, value);
            }
        }
    }

    /// <summary>Gets whether the chosen printer prints on both sides (the choice is hidden when not).</summary>
    public bool SupportsDuplex
    {
        get => _supportsDuplex;
        private set => SetProperty(ref _supportsDuplex, value);
    }

    /// <summary>Gets the choices for the paper orientation.</summary>
    public IReadOnlyList<SettingsChoice<PrintOrientation>> OrientationChoices { get; }

    /// <summary>Gets or sets the paper orientation.</summary>
    public SettingsChoice<PrintOrientation> SelectedOrientation
    {
        get => _selectedOrientation;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedOrientation, value);
            }
        }
    }

    /// <summary>Gets whether "Print" can be chosen: a printer, a valid range and a valid number of copies.</summary>
    public bool CanPrint => SelectedPrinter is not null && RangeError is null && CopiesError is null && !_closed;

    /// <summary>"Print": raises <see cref="Confirmed"/>.</summary>
    public ICommand PrintCommand { get; }

    /// <summary>"Cancel", Esc and the close button: raises <see cref="Cancelled"/>.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>The dialog was shown: fetches the printers in the background.</summary>
    public void OnOpened()
    {
        if (!_closed)
        {
            _ = LoadPrintersAsync();
        }
    }

    /// <summary>The dialog was closed: answers that arrive later are dropped.</summary>
    public void OnClosed()
    {
        _closed = true;
        _lifetime.Cancel();
        _duplexCheck?.Cancel();
        Changed();
    }

    /// <summary>Closes the dialog's view model and releases what it waits with.</summary>
    public void Dispose()
    {
        OnClosed();
        _duplexCheck?.Dispose();
        _lifetime.Dispose();
    }

    private async Task LoadPrintersAsync()
    {
        IsSearching = true;
        SearchFailed = null;
        try
        {
            var printers = await _service.GetPrintersAsync(_lifetime.Token);
            if (_closed)
            {
                return;
            }

            Printers = [.. printers.Select(printer => new SettingsChoice<string>(printer.Name, Describe(printer)))];
            SelectedPrinter = Printers.FirstOrDefault(choice => choice.Value == DefaultPrinter)
                ?? Printers.FirstOrDefault(choice => printers.Any(printer => printer.IsDefault && printer.Name == choice.Value))
                ?? (Printers.Count > 0 ? Printers[0] : null);
        }
        catch (OperationCanceledException)
        {
            // The dialog closed while the list was on its way.
        }
        catch (PrintException ex)
        {
            if (!_closed)
            {
                SearchFailed = ex.Message;
            }
        }
        finally
        {
            if (!_closed)
            {
                IsSearching = false;
            }
        }
    }

    private async Task CheckDuplexAsync(string? printer)
    {
        _duplexCheck?.Cancel();
        if (printer is null || _closed)
        {
            return;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _duplexCheck = source;
        try
        {
            var supported = await _service.SupportsDuplexAsync(printer, source.Token);
            if (!source.IsCancellationRequested && SelectedPrinter?.Value == printer)
            {
                SupportsDuplex = supported;
            }
        }
        catch (OperationCanceledException)
        {
            // Another printer was chosen, or the dialog closed.
        }
        catch (PrintException)
        {
            // Not knowing is the same as no: the choice stays hidden.
        }
    }

    private static string Describe(PrinterInfo printer) =>
        printer.Description is { Length: > 0 } description && description != printer.Name ? $"{printer.Name} – {description}" : printer.Name;

    private void Validate()
    {
        string? rangeError = null;
        if (_pageChoice == PrintPageChoice.Range)
        {
            try
            {
                PageRange.Parse(_rangeText, _pageCount);
            }
            catch (FormatException)
            {
                rangeError = Strings.PrintRangeInvalid;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                rangeError = string.Format(Strings.Culture, CompositeFormat.Parse(Strings.PrintRangeBeyond), ex.ActualValue, _pageCount);
            }
        }

        RangeError = rangeError;
        CopiesError = TryParseCopies(out _) ? null : Strings.PrintCopiesInvalid;
        Changed();
    }

    private bool TryParseCopies(out int copies) =>
        int.TryParse(_copiesText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out copies)
        && copies is >= PrintSettings.MinCopies and <= PrintSettings.MaxCopies;

    private void Changed()
    {
        OnPropertyChanged(nameof(CanPrint));
        _printCommand?.RaiseCanExecuteChanged();
    }

    private void Confirm()
    {
        if (!CanPrint || !TryParseCopies(out var copies))
        {
            return;
        }

        var settings = new PrintSettings
        {
            Printer = SelectedPrinter!.Value,
            Pages = _pageChoice,
            Range = _pageChoice == PrintPageChoice.Range ? _rangeText.Trim() : string.Empty,
            Copies = copies,
            Collate = _collate,
            Scaling = _selectedScaling.Value,
            Duplex = SupportsDuplex ? _selectedDuplex.Value : PrintDuplex.Default,
            Orientation = _selectedOrientation.Value,
        };
        Confirmed?.Invoke(new PrintTicket(settings));
    }
}
