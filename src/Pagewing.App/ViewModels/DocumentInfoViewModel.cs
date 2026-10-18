using System.ComponentModel;
using System.Globalization;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>One line of the document info: a label and its value.</summary>
/// <param name="Label">The name of the value in the UI language ("File size").</param>
/// <param name="Value">The value as shown; never empty.</param>
public sealed record DocumentInfoRow(string Label, string Value);

/// <summary>
/// The content of the document info popup of the status bar (design system 7.6): file name and folder, page count,
/// format of the current page, PDF version, file size, and title, author and creation date of the metadata. A value
/// that is missing is left out, never shown empty. Created when the popup opens and disposed when it closes; it reads
/// the metadata only then (<see cref="LoadAsync"/>), never when the document opens. UI thread only.
/// </summary>
public sealed class DocumentInfoViewModel : ViewModelBase, IDisposable
{
    private const double UnitStep = 1024;

    // Units of the file size; the same in both UI languages.
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB"];

    private readonly TabViewModel _tab;
    private readonly PdfDocument? _document;
    private readonly string? _path;
    private DocumentInfo? _info;
    private bool _isLoaded;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<DocumentInfoRow> _rows = [];
    private bool _disposed;

    /// <summary>Shows the document of <paramref name="tab"/> as it is now; what needs the metadata follows <see cref="LoadAsync"/>.</summary>
    public DocumentInfoViewModel(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _tab = tab;
        _document = tab.Document;
        _path = tab.FullPath;
        _tab.PropertyChanged += OnTabPropertyChanged;
        UpdateRows();
    }

    /// <summary>Gets the lines to show, in their order; values that are missing are left out.</summary>
    public IReadOnlyList<DocumentInfoRow> Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>Gets whether the metadata read has ended, with a result or a failure (then only the file facts show).</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        private set => SetProperty(ref _isLoaded, value);
    }

    /// <summary>
    /// Gets <paramref name="bytes"/> as a size for people: below 1 KB in bytes ("512 B"), otherwise in KB, MB or GB of
    /// 1024 each, with one decimal below 10 ("1.5 MB", "1,5 MB" in German) and as a whole number from 10 on ("153 KB").
    /// A value that would round to 1024 is given in the next unit ("1 MB", not "1024 KB"). Numbers follow
    /// <paramref name="culture"/>, without group separators.
    /// </summary>
    public static string FormatFileSize(long bytes, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        bytes = Math.Max(0, bytes);
        if (bytes < UnitStep)
        {
            return string.Create(culture, $"{bytes} {SizeUnits[0]}");
        }

        var value = bytes / UnitStep;
        var unit = 1;
        while (true)
        {
            var rounded = value < 10
                ? Math.Round(value, 1, MidpointRounding.AwayFromZero)
                : Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded < UnitStep || unit == SizeUnits.Length - 1)
            {
                return string.Create(culture, $"{rounded.ToString(rounded < 10 ? "0.#" : "0", culture)} {SizeUnits[unit]}");
            }

            value /= UnitStep;
            unit++;
        }
    }

    /// <summary>
    /// Reads the metadata and the file size of the document of the tab (<see cref="TabViewModel.InfoReader"/>) and adds
    /// them to <see cref="Rows"/>. A result that arrives after the tab moved on to another document, or after
    /// <see cref="Dispose"/>, is dropped; a failure leaves the file facts and never throws.
    /// </summary>
    /// <returns>Completes when the read has ended; it never fails. Without a document, or once read, it is complete at once.</returns>
    public async Task LoadAsync()
    {
        if (_disposed || IsLoaded || _cancellation is not null || _document is not { } document || _path is not { } path)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        DocumentInfo? info = null;
        try
        {
            info = await _tab.InfoReader(document, path, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Closed meanwhile (Dispose): nothing to show.
            return;
        }
        catch (Exception)
        {
            // A damaged information dictionary, a document closed meanwhile: the file facts stay.
        }

        if (_disposed || !ReferenceEquals(cancellation, _cancellation) || !ReferenceEquals(document, _tab.Document))
        {
            return;
        }

        _cancellation = null;
        cancellation.Dispose();
        _info = info;
        IsLoaded = true;
        UpdateRows();
    }

    /// <summary>Stops following the tab and cancels a read that is still running. May be called more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tab.PropertyChanged -= OnTabPropertyChanged;

        // Cleared before cancelling: a continuation that runs inside Cancel sees that it is stale.
        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.CurrentPageSize) or nameof(TabViewModel.PageCount))
        {
            UpdateRows();
        }
    }

    /// <summary>Builds <see cref="Rows"/> from what is known now; the list is replaced only when a line changed.</summary>
    private void UpdateRows()
    {
        // Only the document this popup was opened for: another one closes the popup (status bar).
        var current = _document is not null && ReferenceEquals(_document, _tab.Document);
        var culture = Strings.Culture;
        var rows = new List<DocumentInfoRow>();

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new DocumentInfoRow(label, value.Trim()));
            }
        }

        Add(Strings.InfoFile, _path is { } path ? Path.GetFileName(path) : null);
        Add(Strings.InfoFolder, _path is { } folderPath ? Path.GetDirectoryName(folderPath) : null);
        var pageCount = current ? _tab.PageCount : 0;
        Add(Strings.InfoPages, pageCount > 0 ? pageCount.ToString("N0", culture) : null);
        Add(Strings.InfoPageSize, current && _tab.CurrentPageSize is { } size ? PageFormat.Describe(size, culture) : null);
        Add(Strings.InfoPdfVersion, current ? VersionOf(_document!) : null);
        if (_info is { } info)
        {
            Add(Strings.InfoFileSize, info.FileSizeBytes > 0 ? FormatFileSize(info.FileSizeBytes, culture) : null);
            Add(Strings.InfoTitle, info.Title);
            Add(Strings.InfoAuthor, info.Author);
            Add(Strings.InfoCreated, info.Created is { } created ? created.ToString("d", culture) : null);
        }

        if (!rows.SequenceEqual(Rows))
        {
            Rows = rows;
        }
    }

    /// <summary>The PDF version of the file header; a field of the document, no PDFium call.</summary>
    private static string? VersionOf(PdfDocument document)
    {
        try
        {
            return document.PdfVersion;
        }
        catch (ObjectDisposedException)
        {
            // Replaced meanwhile; the popup closes.
            return null;
        }
    }
}
