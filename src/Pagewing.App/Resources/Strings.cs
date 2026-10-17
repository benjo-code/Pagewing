using System.Globalization;
using System.Resources;

namespace Pagewing.App.Resources;

/// <summary>
/// UI texts in English and German. Both languages live in the main assembly (no satellite assemblies, which would
/// need runtime assembly loading under Native AOT); <see cref="Culture"/> picks the language.
/// </summary>
public static class Strings
{
    // Logical names set in Pagewing.App.csproj. Each ResourceManager only ever sees the invariant culture.
    private static readonly ResourceManager English = new("Pagewing.App.Resources.Strings.en", typeof(Strings).Assembly);
    private static readonly ResourceManager German = new("Pagewing.App.Resources.Strings.de", typeof(Strings).Assembly);

    private static volatile ResourceManager _current = Select(CultureInfo.CurrentUICulture);
    private static CultureInfo _culture = CultureInfo.CurrentUICulture;

    /// <summary>
    /// The UI language. Starts as <see cref="CultureInfo.CurrentUICulture"/>; German for the two-letter language
    /// <c>de</c>, English for everything else.
    /// </summary>
    public static CultureInfo Culture
    {
        get => _culture;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _culture = value;
            _current = Select(value);
        }
    }

    public static string AppName => Get("AppName");
    public static string OpenFile => Get("OpenFile");
    public static string OpenFileTooltip => Get("OpenFileTooltip");
    public static string PdfFileType => Get("PdfFileType");
    public static string CloseTab => Get("CloseTab");
    public static string CloseTabTooltip => Get("CloseTabTooltip");
    public static string CloseTabMenu => Get("CloseTabMenu");
    public static string MoveToNewWindow => Get("MoveToNewWindow");
    public static string AllTabs => Get("AllTabs");
    public static string AllTabsTooltip => Get("AllTabsTooltip");
    public static string StartTitle => Get("StartTitle");
    public static string StartHint => Get("StartHint");
    public static string StartOffline => Get("StartOffline");
    public static string StartDropHint => Get("StartDropHint");
    public static string ChooseFile => Get("ChooseFile");
    public static string ChooseFileShortcut => Get("ChooseFileShortcut");
    public static string PasswordTitle => Get("PasswordTitle");
    public static string PasswordPrompt => Get("PasswordPrompt");
    public static string PasswordIncorrect => Get("PasswordIncorrect");
    public static string PasswordField => Get("PasswordField");
    public static string PasswordCancel => Get("PasswordCancel");
    public static string PasswordOpen => Get("PasswordOpen");
    public static string ErrorTitle => Get("ErrorTitle");
    public static string ErrorCause => Get("ErrorCause");
    public static string ErrorCauseNoFile => Get("ErrorCauseNoFile");
    public static string ErrorNotAPdf => Get("ErrorNotAPdf");
    public static string ErrorFileNotFound => Get("ErrorFileNotFound");
    public static string ErrorCannotRead => Get("ErrorCannotRead");
    public static string ErrorUnsupportedEncryption => Get("ErrorUnsupportedEncryption");
    public static string ErrorUnknown => Get("ErrorUnknown");
    public static string ErrorDetails => Get("ErrorDetails");
    public static string ErrorDetailsPath => Get("ErrorDetailsPath");
    public static string ErrorDetailsKind => Get("ErrorDetailsKind");
    public static string ErrorDetailsNoPath => Get("ErrorDetailsNoPath");
    public static string ErrorDetailsMessage => Get("ErrorDetailsMessage");
    public static string ErrorCloseTab => Get("ErrorCloseTab");
    public static string ErrorOpenOther => Get("ErrorOpenOther");
    public static string PageLabel => Get("PageLabel");
    public static string PageOf => Get("PageOf");
    public static string PreviousPage => Get("PreviousPage");
    public static string NextPage => Get("NextPage");
    public static string ZoomOut => Get("ZoomOut");
    public static string ZoomIn => Get("ZoomIn");
    public static string ZoomValue => Get("ZoomValue");
    public static string ZoomMenuTooltip => Get("ZoomMenuTooltip");
    public static string ZoomFitWidth => Get("ZoomFitWidth");
    public static string ZoomFitWidthShortcut => Get("ZoomFitWidthShortcut");
    public static string ZoomFitPage => Get("ZoomFitPage");
    public static string ZoomFitPageShortcut => Get("ZoomFitPageShortcut");
    public static string ZoomActualSize => Get("ZoomActualSize");
    public static string ZoomActualSizeShortcut => Get("ZoomActualSizeShortcut");
    public static string GoToPageTooltip => Get("GoToPageTooltip");
    public static string LayoutSingle => Get("LayoutSingle");
    public static string LayoutContinuous => Get("LayoutContinuous");
    public static string LayoutTwoPages => Get("LayoutTwoPages");
    public static string LayoutTwoPagesCover => Get("LayoutTwoPagesCover");
    public static string LayoutTooltip => Get("LayoutTooltip");
    public static string PagePlaceholderFailed => Get("PagePlaceholderFailed");
    public static string PagesPanelTitle => Get("PagesPanelTitle");
    public static string ContentsPanelTitle => Get("ContentsPanelTitle");
    public static string ClosePanel => Get("ClosePanel");
    public static string PagesTooltip => Get("PagesTooltip");
    public static string ContentsTooltip => Get("ContentsTooltip");
    public static string ThumbnailList => Get("ThumbnailList");
    public static string ThumbnailName => Get("ThumbnailName");
    public static string ThumbnailCurrentSuffix => Get("ThumbnailCurrentSuffix");

    private static ResourceManager Select(CultureInfo culture) =>
        culture.TwoLetterISOLanguageName == "de" ? German : English;

    private static string Get(string name) =>
        _current.GetString(name, CultureInfo.InvariantCulture) ?? name;
}
