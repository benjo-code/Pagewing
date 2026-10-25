using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Pagewing.App.Resources;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// Strings.Culture is process-wide state; tests in one class run sequentially, but other test classes
// that read Strings must join this collection.
[Collection("Localization")]
public sealed partial class LocalizationTests : IDisposable
{
    private readonly CultureInfo _originalCulture = Strings.Culture;

    public void Dispose() => Strings.Culture = _originalCulture;

    [Fact]
    public void BothLanguagesDefineTheSameKeys()
    {
        var english = ReadKeys("Strings.en.resx").Keys.Order(StringComparer.Ordinal);
        var german = ReadKeys("Strings.de.resx").Keys.Order(StringComparer.Ordinal);

        Assert.Equal(english, german);
    }

    [Fact]
    public void EveryKeyHasAnAccessorAndEveryAccessorAKey()
    {
        var keys = ReadKeys("Strings.en.resx").Keys.Order(StringComparer.Ordinal).ToArray();
        var accessors = AccessorProperties().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(keys);
        Assert.Equal(keys, accessors);
    }

    [Fact]
    public void KnownKeysAreEmbeddedInTheAssembly()
    {
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Pagewing", Strings.AppName);
        Assert.Equal("Choose file", Strings.ChooseFile);

        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Datei auswählen", Strings.ChooseFile);
    }

    [Fact]
    public void GermanDiffersFromEnglishForUiTexts()
    {
        var english = ReadKeys("Strings.en.resx");
        var german = ReadKeys("Strings.de.resx");
        // The name of the app, and the colour "Orange" of the markup palette, which German writes as English does.
        var same = english.Where(pair => pair.Key is not ("AppName" or "MarkupColorOrange") && german[pair.Key] == pair.Value)
            .Select(pair => pair.Key)
            .ToArray();

        Assert.Empty(same);

        // The accessors really return the text of the resx for the selected language.
        foreach (var accessor in AccessorProperties())
        {
            Strings.Culture = new CultureInfo("en");
            var en = (string)accessor.GetValue(null)!;
            Strings.Culture = new CultureInfo("de");
            var de = (string)accessor.GetValue(null)!;

            Assert.Equal(english[accessor.Name], en);
            Assert.Equal(german[accessor.Name], de);
        }
    }

    [Fact]
    public void UnknownLanguageFallsBackToEnglish()
    {
        Strings.Culture = new CultureInfo("fr");

        Assert.Equal("Choose file", Strings.ChooseFile);
    }

    [Fact]
    public void RegionalGermanCultureUsesGerman()
    {
        Strings.Culture = new CultureInfo("de-AT");

        Assert.Equal("Datei auswählen", Strings.ChooseFile);
    }

    [Fact]
    public void PlaceholdersMatchBetweenLanguages()
    {
        var english = ReadKeys("Strings.en.resx");
        var german = ReadKeys("Strings.de.resx");

        foreach (var (key, value) in english)
        {
            Assert.Equal(Placeholders(value), Placeholders(german[key]));
        }

        Assert.Equal(["{0}"], Placeholders(english["PasswordPrompt"]));
        Assert.Equal(["{0}"], Placeholders(english["PageOf"]));
        Assert.Equal(["{0}"], Placeholders(english["SearchSummaryOne"]));
        Assert.Equal(["{0}", "{1}"], Placeholders(english["SearchSummaryOnePage"]));
        Assert.Equal(["{0}", "{1}"], Placeholders(english["SearchSummaryPages"]));
        Assert.Equal(["{0}"], Placeholders(english["SearchLimit"]));
        Assert.Equal(["{0}", "{1}"], Placeholders(english["SearchHitName"]));
        Assert.Equal(["{0}"], Placeholders(english["LinkWarningUserInfo"]));
        Assert.Equal(["{0}"], Placeholders(english["LinkWarningPunycode"]));
        Assert.Equal(["{0}"], Placeholders(english["StartRecentRemoveName"]));
    }

    [Fact]
    public void SearchKeysAreEmbeddedInTheAssembly()
    {
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Search", Strings.SearchPanelTitle);
        Assert.Equal("Search in document", Strings.SearchPlaceholder);
        Assert.Equal("Searching …", Strings.SearchRunning);

        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Suche", Strings.SearchPanelTitle);
        Assert.Equal("Im Dokument suchen", Strings.SearchPlaceholder);
        Assert.Equal("Keine Treffer", Strings.SearchNone);
    }

    [Fact]
    public void TextSelectionKeysAreEmbeddedInTheAssembly()
    {
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("There is no selectable text on this page. It may be a scan.", Strings.NoTextOnPage);

        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Auf dieser Seite gibt es keinen auswählbaren Text. Sie ist vielleicht ein Scan.", Strings.NoTextOnPage);
    }

    [Fact]
    public void LinkKeysAreEmbeddedInTheAssembly()
    {
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Open this web address?", Strings.LinkConfirmTitle);
        Assert.Equal("Cancel", Strings.LinkConfirmCancel);

        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Diese Webadresse öffnen?", Strings.LinkConfirmTitle);
        Assert.Equal("Öffnen", Strings.LinkConfirmOpen);
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderPattern();

    private static string[] Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();

    private static Dictionary<string, string> ReadKeys(string fileName)
    {
        var path = Path.Combine(TestData.Root, "src", "Pagewing.App", "Resources", fileName);
        var document = XDocument.Load(path);

        return document.Root!.Elements("data")
            .ToDictionary(
                data => (string)data.Attribute("name")!,
                data => (string)data.Element("value")!,
                StringComparer.Ordinal);
    }

    private static IEnumerable<PropertyInfo> AccessorProperties() =>
        typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string));
}
