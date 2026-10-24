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
}
