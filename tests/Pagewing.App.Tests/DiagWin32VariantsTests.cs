// DIAG-TEMP: tests of the temporary diagnostics of the hardware measurement matrix; removed with them (git revert).
using Avalonia;
using Pagewing.App.Platform;
using Xunit;

namespace Pagewing.App.Tests;

public sealed class DiagWin32VariantsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("software")]
    [InlineData("no-such-variant")]
    public void OutsideAMeasurementTheVariableIsIgnored(string? value)
    {
        Assert.Equal((null, null), DiagWin32Variants.Resolve(value, isMeasurement: false, isWindows: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t")]
    public void AMeasurementWithoutAValueUsesNoVariant(string? value)
    {
        Assert.Equal((null, null), DiagWin32Variants.Resolve(value, isMeasurement: true, isWindows: true));
    }

    [Fact]
    public void EveryKnownVariantIsAcceptedInAMeasurementAndApplies()
    {
        Assert.Equal(DiagWin32Variants.Names.Count, DiagWin32Variants.Names.Distinct(StringComparer.Ordinal).Count());
        foreach (var name in DiagWin32Variants.Names)
        {
            Assert.Equal((name, null), DiagWin32Variants.Resolve($" {name} ", isMeasurement: true, isWindows: true));
            Assert.NotNull(DiagWin32Variants.Apply(AppBuilder.Configure<App>(), name, TextWriter.Null));
        }
    }

    [Theory]
    [InlineData("no-such-variant")]
    [InlineData("Software")]
    [InlineData("adapter-3")]
    public void AnUnknownVariantFailsAMeasurementLoudly(string value)
    {
        var (variant, error) = DiagWin32Variants.Resolve(value, isMeasurement: true, isWindows: true);

        Assert.Null(variant);
        Assert.StartsWith($"unknown PAGEWING_DIAG_WIN32 value '{value}' (known: default, software,", error, StringComparison.Ordinal);
    }

    [Fact]
    public void OffWindowsAnyVariantFailsAMeasurement()
    {
        var (variant, error) = DiagWin32Variants.Resolve("default", isMeasurement: true, isWindows: false);

        Assert.Null(variant);
        Assert.Contains("only on Windows", error, StringComparison.Ordinal);
    }
}
