using System.Globalization;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The card "Export pages as images" (milestone 13, spec 5.3 and 5.4): PNG files, one per page, named
/// <c>&lt;name&gt;-p&lt;NNN&gt;.png</c> like <c>pagewing render</c>, of the selected pages or of all pages, at a resolution
/// (default 144 dpi; 36 to 1200), into a folder. The pages are drawn light, with comments and form fields as shown. A page above
/// 64 megapixels is refused by its number before any page is written. Nothing that exists is replaced unless the user turns it on.
/// UI thread only.
/// </summary>
public sealed class ExportImagesViewModel : PageOutputViewModel
{
    /// <summary>The lowest resolution of an export, in dots per inch.</summary>
    public const int MinDpi = 36;

    /// <summary>The highest resolution of an export, in dots per inch.</summary>
    public const int MaxDpi = 1200;

    /// <summary>The resolution the card starts with: twice the size of the page at 72 dpi (what <c>pagewing render</c> does).</summary>
    public const int DefaultDpi = 144;

    private readonly RelayCommand _startCommand;
    private string _dpiText = DefaultDpi.ToString(CultureInfo.InvariantCulture);
    private string? _dpiError;
    private bool _exportSelection;

    /// <summary>Creates the card.</summary>
    /// <param name="source">The open document, its file and the selection of the list.</param>
    /// <param name="pickFolder">Shows the folder dialog.</param>
    internal ExportImagesViewModel(PageOutputSource source, Func<string?, Task<string?>> pickFolder)
        : base(source, pickFolder, PageOutputState.Setup)
    {
        _exportSelection = source.Selection.Count > 0;
        _startCommand = new RelayCommand(() => _ = StartAsync(), () => IsSetup && _dpiError is null);
    }

    /// <summary>Gets or sets the resolution in dpi as typed; a whole number from <see cref="MinDpi"/> to <see cref="MaxDpi"/>.</summary>
    public string DpiText
    {
        get => _dpiText;
        set
        {
            if (SetProperty(ref _dpiText, value ?? string.Empty))
            {
                DpiError = TryDpi(_dpiText, out _) ? null : Format(Strings.ExportDpiInvalid, MinDpi, MaxDpi);
                _startCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets why the resolution cannot be taken, or <see langword="null"/>.</summary>
    public string? DpiError
    {
        get => _dpiError;
        private set => SetProperty(ref _dpiError, value);
    }

    /// <summary>Gets whether pages are selected in the list.</summary>
    public bool HasSelection => Source.Selection.Count > 0;

    /// <summary>Gets or sets whether only the selected pages are exported; starts true with a selection.</summary>
    public bool ExportSelection
    {
        get => _exportSelection;
        set
        {
            if (value && !HasSelection)
            {
                return;
            }

            if (SetProperty(ref _exportSelection, value))
            {
                OnPropertyChanged(nameof(ExportAll));
            }
        }
    }

    /// <summary>Gets or sets whether all pages are exported (the other choice).</summary>
    public bool ExportAll
    {
        get => !_exportSelection;
        set
        {
            if (value)
            {
                ExportSelection = false;
            }
            else if (HasSelection)
            {
                ExportSelection = true;
            }
        }
    }

    /// <summary>Gets the words of the choice "selected pages" with their count.</summary>
    public string SelectedText => Format(Strings.ExportSelected, Source.Selection.Count.ToString("N0", Strings.Culture));

    /// <summary>Gets the words of the choice "all pages" with their count.</summary>
    public string AllText => Format(Strings.ExportAll, Source.PageCount.ToString("N0", Strings.Culture));

    /// <summary>"Export": writes the images. Runs only with a resolution that is valid.</summary>
    public ICommand StartCommand => _startCommand;

    private protected override void OnStateChanged() => _startCommand.RaiseCanExecuteChanged();

    private protected override string SuccessText(IReadOnlyList<string> paths) =>
        paths.Count == 1
            ? Format(Strings.PageOutputExportDoneOne, Path.GetDirectoryName(paths[0]) ?? Folder)
            : Format(Strings.PageOutputExportDoneMany, paths.Count.ToString("N0", Strings.Culture), Path.GetDirectoryName(paths[0]) ?? Folder);

    private protected override string Describe(Exception exception)
    {
        switch (exception)
        {
            case PageExportException export:
                var page = Math.Max(export.Page, 0) + 1;
                switch (export.Error)
                {
                    case PageExportError.TooLarge:
                        return Format(Strings.PageOutputExportTooLarge, page.ToString("N0", Strings.Culture), TryDpi(_dpiText, out var dpi) ? dpi : DefaultDpi);
                    case PageExportError.DirectoryMissing:
                        return Strings.PageOutputErrorFolderMissing;
                    case PageExportError.TargetExists:
                        return Format(Strings.PageOutputErrorTargetExists, NamesOf(export.Paths));
                    case PageExportError.TargetIsSource:
                        return Strings.PageOutputErrorTargetIsSource;
                    case PageExportError.UnsafeName:
                        return Strings.PageOutputErrorUnsafeName;
                    case PageExportError.TooManyPages:
                        return Strings.PageOutputErrorNotPossible;
                    default:
                        // A page that could not be written: the cause in everyday words, never the text of the exception (a path).
                        var cause = export.InnerException is { } inner ? DescribeCommon(inner) ?? Strings.SaveErrorWriteFailed : Strings.SaveErrorWriteFailed;
                        return Format(Strings.PageOutputExportWriteFailed, page.ToString("N0", Strings.Culture), cause);
                }

            case PdfRenderException:
                return Strings.PageOutputExportRenderFailed;
            default:
                return DescribeCommon(exception) ?? Strings.PageOutputErrorUnknown;
        }
    }

    private static bool TryDpi(string text, out int dpi) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out dpi) && dpi is >= MinDpi and <= MaxDpi;

    private async Task StartAsync()
    {
        if (!IsSetup || !TryDpi(_dpiText, out var dpi))
        {
            return;
        }

        FolderError = null;
        var folder = Folder.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            FolderError = Strings.PageOutputErrorFolderMissing;
            return;
        }

        var pages = _exportSelection ? Source.Selection : [.. Enumerable.Range(0, Source.PageCount)];
        var overwrite = ReplaceExisting;
        var fullFolder = Path.GetFullPath(folder);
        await RunAsync((progress, written, token) =>
            PageImageExporter.ExportAsync(
                Source.Document,
                Source.Stem,
                pages,
                dpi / 72.0,
                annotations: true,
                fullFolder,
                overwrite,
                progress,
                token,
                written,
                sourcePath: Source.File.Path));
    }
}
