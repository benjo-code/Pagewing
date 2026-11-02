using Pagewing.Core.Annotations;
using Pagewing.Core.Printing;
using Pagewing.Core.Settings;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        var settings = AppSettings.Default;

        Assert.Equal(1, settings.Version);
        Assert.Equal(ThemeSetting.System, settings.Theme);
        Assert.Equal(LanguageSetting.System, settings.Language);
        Assert.Equal(DensitySetting.Normal, settings.Density);
        Assert.False(settings.ReduceMotion);
        Assert.Equal(PageAppearanceSetting.Light, settings.PageAppearance);
        Assert.Equal(PageLayoutMode.Continuous, settings.DefaultLayout);
        Assert.Equal(ZoomMode.ActualSize, settings.DefaultZoomMode);
        Assert.Equal(StartPanelSetting.Pages, settings.StartPanel);
        Assert.Equal(264d, settings.PanelWidth);
    }

    [Fact]
    public void UpdateCheckIsOnByDefault()
    {
        Assert.True(AppSettings.Default.UpdateCheck);
        Assert.True(new AppSettings().UpdateCheck);
        Assert.True(AppSettings.Default.Normalized().UpdateCheck);
        Assert.False((AppSettings.Default with { UpdateCheck = false }).Normalized().UpdateCheck);
    }

    [Fact]
    public void UpdateCheckSurvivesTheRoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pagewing-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));

            Assert.True(store.TrySave(AppSettings.Default with { UpdateCheck = false }));
            Assert.False(store.Load().UpdateCheck);

            Assert.True(store.TrySave(AppSettings.Default with { UpdateCheck = true }));
            Assert.True(store.Load().UpdateCheck);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingUpdateCheckFieldKeepsTheDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pagewing-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ \"Theme\": \"Dark\" }");

            var loaded = new SettingsStore(path).Load();

            Assert.Equal(ThemeSetting.Dark, loaded.Theme);
            Assert.True(loaded.UpdateCheck);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Büro Drucker", "Büro Drucker")]
    public void NormalizedKeepsOnlyAUsablePrinterName(string? name, string? expected)
    {
        Assert.Equal(expected, (AppSettings.Default with { LastPrinter = name }).Normalized().LastPrinter);
        Assert.Null((AppSettings.Default with { LastPrinter = new string('x', AppSettings.MaxPrinterNameLength + 1) }).Normalized().LastPrinter);
    }

    [Fact]
    public void NormalizedClampsPanelWidthAndFixesUndefinedValues()
    {
        Assert.Equal(400d, (AppSettings.Default with { PanelWidth = 5000 }).Normalized().PanelWidth);
        Assert.Equal(200d, (AppSettings.Default with { PanelWidth = 10 }).Normalized().PanelWidth);
        Assert.Equal(264d, (AppSettings.Default with { PanelWidth = double.NaN }).Normalized().PanelWidth);
        Assert.Equal(264d, (AppSettings.Default with { PanelWidth = double.PositiveInfinity }).Normalized().PanelWidth);
        Assert.Equal(300d, (AppSettings.Default with { PanelWidth = 300 }).Normalized().PanelWidth);

        var broken = AppSettings.Default with
        {
            Theme = (ThemeSetting)99,
            Language = (LanguageSetting)99,
            Density = (DensitySetting)99,
            PageAppearance = (PageAppearanceSetting)99,
            DefaultLayout = (PageLayoutMode)99,
            DefaultZoomMode = ZoomMode.Free,
            StartPanel = (StartPanelSetting)99,
            PrintScaling = (PrintScaling)99,
            DefaultAppPrompt = (DefaultAppPromptSetting)99,
        };
        var fixedSettings = broken.Normalized();

        Assert.Equal(ThemeSetting.System, fixedSettings.Theme);
        Assert.Equal(LanguageSetting.System, fixedSettings.Language);
        Assert.Equal(DensitySetting.Normal, fixedSettings.Density);
        Assert.Equal(PageAppearanceSetting.Light, fixedSettings.PageAppearance);
        Assert.Equal(PageLayoutMode.Continuous, fixedSettings.DefaultLayout);
        Assert.Equal(ZoomMode.ActualSize, fixedSettings.DefaultZoomMode);
        Assert.Equal(PrintScaling.FitToPaper, fixedSettings.PrintScaling);
        Assert.Equal(StartPanelSetting.Pages, fixedSettings.StartPanel);
        Assert.Equal(DefaultAppPromptSetting.Ask, fixedSettings.DefaultAppPrompt);
        Assert.Equal(DefaultAppPromptSetting.Ask, AppSettings.Default.DefaultAppPrompt);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Anna Beispiel", "Anna Beispiel")]
    public void AuthorNameIsDroppedWhenEmptyOrTooLong(string? name, string? expected)
    {
        Assert.Null(AppSettings.Default.AuthorName);
        Assert.Equal(expected, (AppSettings.Default with { AuthorName = name }).Normalized().AuthorName);
        Assert.Equal(new string('x', AppSettings.MaxAuthorNameLength), (AppSettings.Default with { AuthorName = new string('x', AppSettings.MaxAuthorNameLength) }).Normalized().AuthorName);
        Assert.Equal(128, AppSettings.MaxAuthorNameLength);
        Assert.Null((AppSettings.Default with { AuthorName = new string('x', AppSettings.MaxAuthorNameLength + 1) }).Normalized().AuthorName);
    }

    [Fact]
    public void InvalidMarkupColorsFallBackToTheDefaults()
    {
        Assert.Equal(MarkupColor.Yellow, AppSettings.Default.HighlightColor);
        Assert.Equal(MarkupColor.Blue, AppSettings.Default.UnderlineColor);
        Assert.Equal(MarkupColor.Pink, AppSettings.Default.StrikeOutColor);

        var fixedSettings = (AppSettings.Default with
        {
            HighlightColor = (MarkupColor)99,
            UnderlineColor = (MarkupColor)(-1),
            StrikeOutColor = (MarkupColor)99,
        }).Normalized();

        Assert.Equal(MarkupColor.Yellow, fixedSettings.HighlightColor);
        Assert.Equal(MarkupColor.Blue, fixedSettings.UnderlineColor);
        Assert.Equal(MarkupColor.Pink, fixedSettings.StrikeOutColor);

        var kept = (AppSettings.Default with
        {
            HighlightColor = MarkupColor.Green,
            UnderlineColor = MarkupColor.Orange,
            StrikeOutColor = MarkupColor.Yellow,
        }).Normalized();

        Assert.Equal(MarkupColor.Green, kept.HighlightColor);
        Assert.Equal(MarkupColor.Orange, kept.UnderlineColor);
        Assert.Equal(MarkupColor.Yellow, kept.StrikeOutColor);
    }

    [Fact]
    public void OldSettingsFileWithoutTheNewFieldsLoadsWithDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pagewing-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ \"Theme\": \"Dark\", \"UpdateCheck\": false }");

            var store = new SettingsStore(path);
            var loaded = store.Load();

            Assert.Equal(ThemeSetting.Dark, loaded.Theme);
            Assert.False(loaded.UpdateCheck);
            Assert.Null(loaded.AuthorName);
            Assert.Equal(MarkupColor.Yellow, loaded.HighlightColor);
            Assert.Equal(MarkupColor.Blue, loaded.UnderlineColor);
            Assert.Equal(MarkupColor.Pink, loaded.StrikeOutColor);

            // And the new values survive a round trip.
            Assert.True(store.TrySave(loaded with { AuthorName = "Anna", HighlightColor = MarkupColor.Green, StrikeOutColor = MarkupColor.Orange }));
            var again = store.Load();
            Assert.Equal("Anna", again.AuthorName);
            Assert.Equal(MarkupColor.Green, again.HighlightColor);
            Assert.Equal(MarkupColor.Blue, again.UnderlineColor);
            Assert.Equal(MarkupColor.Orange, again.StrikeOutColor);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NormalizedKeepsValidValues()
    {
        var settings = new AppSettings
        {
            Theme = ThemeSetting.Dark,
            DefaultZoomMode = ZoomMode.FitPage,
            DefaultLayout = PageLayoutMode.TwoPages,
            StartPanel = StartPanelSetting.None,
        };

        Assert.Equal(settings, settings.Normalized());
    }

    [Fact]
    public void CodesAreLimitedAndNormalized()
    {
        // Nothing is stored while the user changed nothing: the interface shows "geschwärzt" or "redacted" by language.
        Assert.Null(AppSettings.Default.RedactionCodes);
        Assert.Null(AppSettings.Default.Normalized().RedactionCodes);
        Assert.Equal(20, AppSettings.MaxRedactionCodes);
        Assert.Equal(40, AppSettings.MaxRedactionCodeLength);

        var exactly = new string('x', AppSettings.MaxRedactionCodeLength);
        var tooLong = new string('y', AppSettings.MaxRedactionCodeLength + 1);
        var codes = new List<string> { "  Akte 1 ", string.Empty, "   ", tooLong, "Akte 1", "ok ☃", exactly, "Vertraulich" };
        codes.AddRange(Enumerable.Range(0, 30).Select(static i => "Code " + i));

        var normalized = (AppSettings.Default with { RedactionCodes = codes }).Normalized().RedactionCodes;

        // Trimmed, without the empty, the overlong, the ones the overlay cannot draw and the repeated; at most 20; the order stays.
        Assert.NotNull(normalized);
        Assert.Equal(AppSettings.MaxRedactionCodes, normalized.Count);
        Assert.Equal(["Akte 1", exactly, "Vertraulich", "Code 0", "Code 1"], normalized.Take(5));
        Assert.All(normalized, static code => Assert.InRange(code.Length, 1, AppSettings.MaxRedactionCodeLength));
        Assert.Equal(normalized.Count, normalized.Distinct(StringComparer.Ordinal).Count());

        // A list that needs no repair is the same list, so a normalized copy equals its source.
        var clean = (AppSettings.Default with { RedactionCodes = ["a", "b"] });
        Assert.Same(clean.RedactionCodes, clean.Normalized().RedactionCodes);

        // No code left is an empty list, not the default: the user took them all away.
        Assert.Empty((AppSettings.Default with { RedactionCodes = [" ", tooLong] }).Normalized().RedactionCodes!);
        Assert.Empty((AppSettings.Default with { RedactionCodes = [] }).Normalized().RedactionCodes!);
    }

    [Fact]
    public void WithRedactionCodesMakesANewNormalizedListAndNeverAliasesTheOldOne()
    {
        var first = AppSettings.Default.WithRedactionCodes(["  Akte 1", "Akte 1", "Vertraulich"]);
        var source = new List<string> { "Eins" };
        var second = first.WithRedactionCodes(source);
        source.Add("Zwei");

        Assert.Equal(["Akte 1", "Vertraulich"], first.RedactionCodes);
        Assert.Equal(["Eins"], second.RedactionCodes);
        Assert.NotSame(first.RedactionCodes, second.RedactionCodes);
        Assert.Null(first.WithRedactionCodes(null).RedactionCodes);
        Assert.Empty(first.WithRedactionCodes([]).RedactionCodes!);
    }

    [Fact]
    public void SettingsJsonRoundTripKeepsCodes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pagewing-settings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));

            Assert.True(store.TrySave(AppSettings.Default.WithRedactionCodes(["Akte ä", "Vertraulich"])));
            Assert.Equal(["Akte ä", "Vertraulich"], store.Load().RedactionCodes);

            // An untouched file has none, and a file written without the field reads as none.
            Assert.True(store.TrySave(AppSettings.Default));
            Assert.Null(store.Load().RedactionCodes);
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{ \"Theme\": \"Dark\" }");
            Assert.Null(store.Load().RedactionCodes);

            // A hand-edited file is repaired on load: too many, too long and repeated entries do not get in.
            var many = string.Join(", ", Enumerable.Range(0, 40).Select(static i => $"\"c{i}\""));
            File.WriteAllText(Path.Combine(directory, "settings.json"), $"{{ \"RedactionCodes\": [ \"a\", null, \"a\", \"{new string('z', 41)}\", {many} ] }}");
            var repaired = store.Load().RedactionCodes;
            Assert.NotNull(repaired);
            Assert.Equal(AppSettings.MaxRedactionCodes, repaired.Count);
            Assert.Equal(["a", "c0"], repaired.Take(2));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OcrLanguagesAreNormalizedAndDefaultIsNull()
    {
        Assert.Null(AppSettings.Default.OcrLanguages);
        Assert.Equal("deu+eng", (AppSettings.Default with { OcrLanguages = " deu + eng " }).Normalized().OcrLanguages);
        Assert.Null((AppSettings.Default with { OcrLanguages = "../eng" }).Normalized().OcrLanguages);
        Assert.Null((AppSettings.Default with { OcrLanguages = string.Empty }).Normalized().OcrLanguages);
    }
}
