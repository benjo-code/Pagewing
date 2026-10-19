using Pagewing.Core.Printing;
using Xunit;

namespace Pagewing.Core.Tests.Printing;

public sealed class PrintSettingsTests
{
    [Fact]
    public void DefaultsMatchTheDecisions()
    {
        var settings = new PrintSettings();

        Assert.Equal(PrintPageChoice.All, settings.Pages);
        Assert.Equal(1, settings.Copies);
        Assert.True(settings.Collate);
        Assert.Equal(PrintScaling.FitToPaper, settings.Scaling);
        Assert.Equal(PrintDuplex.Default, settings.Duplex);
        Assert.Equal(PrintOrientation.Default, settings.Orientation);
        Assert.Equal(string.Empty, settings.Printer);
    }

    [Fact]
    public void NormalizedFixesUndefinedValues()
    {
        var settings = new PrintSettings
        {
            Copies = 5000,
            Pages = (PrintPageChoice)99,
            Scaling = (PrintScaling)99,
            Duplex = (PrintDuplex)99,
            Orientation = (PrintOrientation)99,
        }.Normalized();

        Assert.Equal(PrintSettings.MaxCopies, settings.Copies);
        Assert.Equal(PrintPageChoice.All, settings.Pages);
        Assert.Equal(PrintScaling.FitToPaper, settings.Scaling);
        Assert.Equal(PrintDuplex.Default, settings.Duplex);
        Assert.Equal(PrintOrientation.Default, settings.Orientation);
    }
}
