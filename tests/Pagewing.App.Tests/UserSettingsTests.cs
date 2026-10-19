using System.Globalization;
using Pagewing.App.Services;
using Pagewing.Core.Settings;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The settings of the app (<see cref="UserSettings"/>). Every test works in a temporary folder or with
/// <see cref="UserSettings.Detached"/>; none touches the real user folder.
/// </summary>
public sealed class UserSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-usersettings-" + Guid.NewGuid().ToString("N"));

    private string SettingsFile => Path.Combine(_directory, UserSettings.FileName);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void DetachedNeverWritesAFile()
    {
        var settings = UserSettings.Detached();
        var changes = new List<AppSettings>();
        settings.Changed += changes.Add;

        settings.Update(current => current with { Theme = ThemeSetting.Dark, PanelWidth = 300 });

        // Kept in memory and announced like any change, but there is no file to write.
        Assert.Equal(ThemeSetting.Dark, settings.Current.Theme);
        Assert.Equal(300, settings.Current.PanelWidth);
        Assert.Single(changes);
        Assert.False(settings.LastSaveFailed);
        Assert.Null(settings.FilePath);
        Assert.Equal(AppSettings.Default, UserSettings.Detached().Current);
    }

    [Fact]
    public void UpdateRaisesChangedAndWrites()
    {
        var settings = UserSettings.Load(_directory);
        var fileExistedWhenNotified = (bool?)null;
        AppSettings? announced = null;
        settings.Changed += value =>
        {
            announced = value;
            fileExistedWhenNotified = File.Exists(SettingsFile);
        };

        settings.Update(current => current with { ReduceMotion = true });

        // Announced first, written afterwards.
        Assert.Equal(settings.Current, announced);
        Assert.True(settings.Current.ReduceMotion);
        Assert.False(fileExistedWhenNotified);
        Assert.True(File.Exists(SettingsFile));
        Assert.False(settings.LastSaveFailed);
        Assert.True(new SettingsStore(SettingsFile).Load().ReduceMotion);

        // A change to the same value is no change: no event, no write.
        File.Delete(SettingsFile);
        announced = null;
        settings.Update(current => current with { ReduceMotion = true });
        Assert.Null(announced);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void FailingSaveKeepsTheValueAndFlagsIt()
    {
        // The config "folder" is a file: the settings cannot be written there.
        File.WriteAllText(_directory + ".blocker", "not a folder");
        try
        {
            var settings = UserSettings.Load(_directory + ".blocker");
            var changes = 0;
            settings.Changed += _ => changes++;

            settings.Update(current => current with { Theme = ThemeSetting.Light });

            Assert.Equal(ThemeSetting.Light, settings.Current.Theme);
            Assert.Equal(1, changes);
            Assert.True(settings.LastSaveFailed);

            // The next successful write clears the flag; here the next write fails again and keeps it.
            settings.Update(current => current with { Theme = ThemeSetting.Dark });
            Assert.Equal(ThemeSetting.Dark, settings.Current.Theme);
            Assert.True(settings.LastSaveFailed);
        }
        finally
        {
            File.Delete(_directory + ".blocker");
        }
    }

    [Fact]
    public void LastSaveFailedClearsAfterALaterSuccessfulWrite()
    {
        // The config "folder" is a file at first: the write fails.
        File.WriteAllText(_directory, "not a folder");
        var settings = UserSettings.Load(_directory);
        settings.Update(current => current with { Density = DensitySetting.Compact });
        Assert.True(settings.LastSaveFailed);

        // The folder can be made now: the next change is written, with every value of memory, and the flag clears.
        File.Delete(_directory);
        settings.Update(current => current with { ReduceMotion = true });
        Assert.False(settings.LastSaveFailed);
        var stored = new SettingsStore(SettingsFile).Load();
        Assert.Equal(DensitySetting.Compact, stored.Density);
        Assert.True(stored.ReduceMotion);
    }

    [Fact]
    public void NoFileMeansNoJsonIsRead()
    {
        Directory.CreateDirectory(_directory);

        var settings = UserSettings.Load(_directory);

        Assert.Equal(AppSettings.Default, settings.Current);
        Assert.False(settings.LastSaveFailed);
        Assert.Equal(SettingsFile, settings.FilePath);

        // Loading writes nothing: no file, no temporary file, no backup.
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void UpdateNormalizes()
    {
        var settings = UserSettings.Detached();
        AppSettings? announced = null;
        settings.Changed += value => announced = value;

        settings.Update(current => current with { PanelWidth = 9000, Theme = (ThemeSetting)42, DefaultZoomMode = Core.Viewing.ZoomMode.Free });

        Assert.Equal(AppSettings.MaxPanelWidth, settings.Current.PanelWidth);
        Assert.Equal(ThemeSetting.System, settings.Current.Theme);
        Assert.Equal(Core.Viewing.ZoomMode.ActualSize, settings.Current.DefaultZoomMode);
        Assert.Equal(settings.Current, announced);
    }

    [Fact]
    public void SettingsSurviveARestart()
    {
        var first = UserSettings.Load(_directory);
        first.Update(current => current with
        {
            Theme = ThemeSetting.Dark,
            Language = LanguageSetting.German,
            ReduceMotion = true,
            StartPanel = StartPanelSetting.Contents,
            PanelWidth = 321,
        });

        // The next start reads what the last one wrote.
        var second = UserSettings.Load(_directory);

        Assert.Equal(first.Current, second.Current);
        Assert.Equal(ThemeSetting.Dark, second.Current.Theme);
        Assert.Equal(LanguageSetting.German, second.Current.Language);
        Assert.True(second.Current.ReduceMotion);
        Assert.Equal(StartPanelSetting.Contents, second.Current.StartPanel);
        Assert.Equal(321, second.Current.PanelWidth);
    }

    [Theory]
    [InlineData("en", LanguageSetting.German, "en")]
    [InlineData("de", LanguageSetting.English, "de")]
    [InlineData(null, LanguageSetting.German, "de")]
    [InlineData(null, LanguageSetting.English, "en")]
    [InlineData(null, LanguageSetting.System, null)]
    [InlineData("not a culture!", LanguageSetting.German, "de")]
    public void LanguageOptionWinsOverTheSettingAndTheSettingOverTheSystem(string? option, LanguageSetting setting, string? expected)
    {
        var culture = Program.ChooseUiCulture(option, setting);

        Assert.Equal(expected, culture?.Name);
        if (expected is not null)
        {
            Assert.Equal(new CultureInfo(expected), culture);
        }
    }
}
