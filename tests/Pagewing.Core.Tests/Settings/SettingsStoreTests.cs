using Pagewing.Core.Annotations;
using Pagewing.Core.Printing;
using Pagewing.Core.Settings;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string FilePath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void MissingFileGivesDefaults()
    {
        Assert.Equal(AppSettings.Default, new SettingsStore(FilePath).Load());
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void RoundTripKeepsEveryField()
    {
        var settings = new AppSettings
        {
            Theme = ThemeSetting.Dark,
            Language = LanguageSetting.German,
            Density = DensitySetting.Compact,
            ReduceMotion = true,
            PageAppearance = PageAppearanceSetting.FollowTheme,
            DefaultLayout = PageLayoutMode.TwoPagesWithCover,
            DefaultZoomMode = ZoomMode.FitWidth,
            StartPanel = StartPanelSetting.Contents,
            PanelWidth = 321.5,
            LastPrinter = "Büro Drucker",
            PrintScaling = PrintScaling.ActualSize,
            DefaultAppPrompt = DefaultAppPromptSetting.Never,
        };
        var store = new SettingsStore(FilePath);

        Assert.True(store.TrySave(settings));

        Assert.Equal(settings, new SettingsStore(FilePath).Load());
    }

    [Fact]
    public void CorruptJsonGivesDefaultsAndKeepsABackup()
    {
        File.WriteAllText(FilePath, "{\"Theme\":");

        var loaded = new SettingsStore(FilePath).Load();

        Assert.Equal(AppSettings.Default, loaded);
        Assert.Equal("{\"Theme\":", File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void LoadReadOnlyReadsTheValuesAndNeverWrites()
    {
        File.WriteAllText(FilePath, "{ \"Theme\": \"Dark\", \"PanelWidth\": 999 }");
        Assert.Equal(AppSettings.Default with { Theme = ThemeSetting.Dark, PanelWidth = AppSettings.MaxPanelWidth }, new SettingsStore(FilePath).LoadReadOnly());

        // A corrupt file gives the defaults like Load, but without the backup.
        File.WriteAllText(FilePath, "{\"Theme\":");
        Assert.Equal(AppSettings.Default, new SettingsStore(FilePath).LoadReadOnly());
        Assert.Equal([FilePath], Directory.GetFiles(_directory));
        Assert.Equal(AppSettings.Default, new SettingsStore(Path.Combine(_directory, "missing.json")).LoadReadOnly());
    }

    [Fact]
    public void AnUnknownEnumValueMakesTheWholeFileCorrupt()
    {
        // Known and accepted (Docs/DECISIONS.md, settings entry): a value of a later version ("Sepia") loses the other
        // values of the file too; they come back as defaults, and the file is kept as .bad.
        File.WriteAllText(FilePath, "{ \"Theme\": \"Sepia\", \"Language\": \"German\" }");

        Assert.Equal(AppSettings.Default, new SettingsStore(FilePath).Load());
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void ASecondCorruptFileOverwritesTheBackup()
    {
        File.WriteAllText(FilePath + ".bad", "old");
        File.WriteAllText(FilePath, "not json");

        new SettingsStore(FilePath).Load();

        Assert.Equal("not json", File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void UnknownFieldsAndFutureVersionAreTolerated()
    {
        File.WriteAllText(
            FilePath,
            """
            {
              // a comment
              "Version": 7,
              "Theme": "Light",
              "Surprise": { "a": [1, 2, 3] },
              "PanelWidth": 300,
            }
            """);

        var loaded = new SettingsStore(FilePath).Load();

        Assert.Equal(ThemeSetting.Light, loaded.Theme);
        Assert.Equal(300d, loaded.PanelWidth);
        Assert.Equal(StartPanelSetting.Pages, loaded.StartPanel);
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void ValuesAreNormalizedOnLoad()
    {
        File.WriteAllText(FilePath, """{ "PanelWidth": 9000, "DefaultZoomMode": "Free", "Theme": 99 }""");

        var loaded = new SettingsStore(FilePath).Load();

        Assert.Equal(400d, loaded.PanelWidth);
        Assert.Equal(ZoomMode.ActualSize, loaded.DefaultZoomMode);
        Assert.Equal(ThemeSetting.System, loaded.Theme);
    }

    [Fact]
    public void MissingFieldsKeepTheirDefaults()
    {
        File.WriteAllText(FilePath, """{"Theme":"Dark"}""");

        Assert.Equal(AppSettings.Default with { Theme = ThemeSetting.Dark }, new SettingsStore(FilePath).Load());
    }

    [Fact]
    public void EmptyFileGivesDefaults()
    {
        File.WriteAllText(FilePath, "");

        Assert.Equal(AppSettings.Default, new SettingsStore(FilePath).Load());
    }

    [Fact]
    public void AnOversizedFileCountsAsCorrupt()
    {
        File.WriteAllText(FilePath, "{ \"Theme\": \"Dark\", \"x\": \"" + new string('a', (1024 * 1024) + 10) + "\" }");

        Assert.Equal(AppSettings.Default, new SettingsStore(FilePath).Load());
        Assert.True(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void AnUnreadablePathGivesDefaults()
    {
        // The path is a directory, so reading it as a file fails.
        Directory.CreateDirectory(FilePath);

        Assert.Equal(AppSettings.Default, new SettingsStore(FilePath).Load());
    }

    [Fact]
    public void SaveIsAtomicAndLeavesNoTempFile()
    {
        var folder = Path.Combine(_directory, "sub");
        var store = new SettingsStore(Path.Combine(folder, "settings.json"));

        Assert.True(store.TrySave(AppSettings.Default with { Theme = ThemeSetting.Light }));
        Assert.True(store.TrySave(AppSettings.Default with { Theme = ThemeSetting.Dark }));

        Assert.Equal(["settings.json"], Directory.GetFiles(folder).Select(Path.GetFileName).ToArray());
        Assert.Equal(ThemeSetting.Dark, store.Load().Theme);
    }

    [Fact]
    public void SaveToAReadOnlyDirectoryReturnsFalse()
    {
        // A file stands where the folder should be, so this fails on every OS.
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");

        Assert.False(new SettingsStore(Path.Combine(blocker, "settings.json")).TrySave(AppSettings.Default));
    }

    [Fact]
    public void EnumsAreWrittenAsText()
    {
        var store = new SettingsStore(FilePath);

        Assert.True(store.TrySave(AppSettings.Default with { Theme = ThemeSetting.Dark, DefaultZoomMode = ZoomMode.FitPage }));

        var text = File.ReadAllText(FilePath);
        Assert.Contains("\"Theme\": \"Dark\"", text, StringComparison.Ordinal);
        Assert.Contains("\"DefaultZoomMode\": \"FitPage\"", text, StringComparison.Ordinal);
        Assert.Contains("\"DefaultLayout\": \"Continuous\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolStylesRoundTripAndFallBackToDefaults()
    {
        var store = new SettingsStore(FilePath);
        var defaults = new ToolStyle(DrawColor.Red, StrokeWidth.Medium);

        // Untouched settings: every tool has the default, and nothing is written for them.
        Assert.Null(AppSettings.Default.ToolStyles);
        foreach (var kind in new[] { AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow, AnnotationKind.TextBox })
        {
            Assert.Equal(defaults, AppSettings.Default.StyleOf(kind));
        }

        Assert.True(store.TrySave(AppSettings.Default));
        Assert.DoesNotContain("Rectangle", File.ReadAllText(FilePath), StringComparison.Ordinal);

        // Written and read with the source generator; a tool without an entry keeps the default.
        var changed = AppSettings.Default with
        {
            ToolStyles = new Dictionary<AnnotationKind, ToolStyle>
            {
                [AnnotationKind.Ink] = new(DrawColor.Blue, StrokeWidth.Thick),
                [AnnotationKind.Arrow] = new(DrawColor.Black, StrokeWidth.Thin),
            },
        };
        Assert.True(store.TrySave(changed));
        Assert.Contains("\"Ink\"", File.ReadAllText(FilePath), StringComparison.Ordinal);
        Assert.Contains("\"Blue\"", File.ReadAllText(FilePath), StringComparison.Ordinal);

        var loaded = store.Load();
        Assert.Equal(new ToolStyle(DrawColor.Blue, StrokeWidth.Thick), loaded.StyleOf(AnnotationKind.Ink));
        Assert.Equal(new ToolStyle(DrawColor.Black, StrokeWidth.Thin), loaded.StyleOf(AnnotationKind.Arrow));
        Assert.Equal(defaults, loaded.StyleOf(AnnotationKind.Rectangle));
        Assert.False(File.Exists(FilePath + ".bad"));

        // A number outside the enums falls back to the default for that value only (the settings are not lost).
        File.WriteAllText(FilePath, "{ \"Theme\": \"Dark\", \"ToolStyles\": { \"Ink\": { \"Color\": 99, \"Width\": \"Thick\" }, \"Line\": { \"Color\": \"Green\", \"Width\": 42 } } }");
        var repaired = store.Load();
        Assert.Equal(ThemeSetting.Dark, repaired.Theme);
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Thick), repaired.StyleOf(AnnotationKind.Ink));
        Assert.Equal(new ToolStyle(DrawColor.Green, StrokeWidth.Medium), repaired.StyleOf(AnnotationKind.Line));
        Assert.False(File.Exists(FilePath + ".bad"));
    }

    [Fact]
    public void ToolStylesOfKindsThatAreNoDrawingToolsAreDroppedOnLoad()
    {
        File.WriteAllText(FilePath, "{ \"ToolStyles\": { \"Highlight\": { \"Color\": \"Blue\", \"Width\": \"Thin\" }, \"99\": { \"Color\": \"Blue\", \"Width\": \"Thin\" }, \"Ellipse\": { \"Color\": \"Orange\", \"Width\": \"Thin\" }, \"Rectangle\": null } }");

        var loaded = new SettingsStore(FilePath).Load();

        Assert.NotNull(loaded.ToolStyles);
        Assert.Equal([AnnotationKind.Ellipse], loaded.ToolStyles.Keys.ToArray());
        Assert.Equal(new ToolStyle(DrawColor.Orange, StrokeWidth.Thin), loaded.StyleOf(AnnotationKind.Ellipse));
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Medium), loaded.StyleOf(AnnotationKind.Rectangle));
    }

    [Theory]
    [InlineData(AnnotationKind.Highlight)]
    [InlineData(AnnotationKind.Underline)]
    [InlineData(AnnotationKind.StrikeOut)]
    [InlineData(AnnotationKind.Other)]
    [InlineData((AnnotationKind)99)]
    public void StyleOfAKindThatIsNoDrawingToolIsTheDefault(AnnotationKind kind)
    {
        var settings = AppSettings.Default with
        {
            ToolStyles = new Dictionary<AnnotationKind, ToolStyle> { [kind] = new(DrawColor.Blue, StrokeWidth.Thick) },
        };

        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Medium), settings.StyleOf(kind));
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Medium), settings.Normalized().StyleOf(kind));
    }

    [Fact]
    public void ToolStylesWithAnUnknownNameCountAsACorruptFile()
    {
        // Like every enum in the file: a name that is not an enum member is a JSON error, so the whole file is
        // kept as .bad and the defaults apply. Nothing throws.
        File.WriteAllText(FilePath, "{ \"ToolStyles\": { \"Ink\": { \"Color\": \"Purple\", \"Width\": \"Thick\" } } }");
        Assert.Equal(AppSettings.Default, new SettingsStore(FilePath).Load());
        Assert.True(File.Exists(FilePath + ".bad"));

        File.WriteAllText(FilePath, "{ \"ToolStyles\": { \"Squiggle\": { \"Color\": \"Red\", \"Width\": \"Thick\" } } }");
        Assert.Equal(AppSettings.Default, new SettingsStore(FilePath).LoadReadOnly());
    }
}
