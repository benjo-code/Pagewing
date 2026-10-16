using Pagewing.Cli;
using Xunit;

namespace Pagewing.Core.Tests.Cli;

public sealed class CancelKeyHandlerTests
{
    [Fact]
    public void FirstPressCancelsGracefullyAndSecondPressTerminates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new CancelKeyHandler(cancellation);

        Assert.True(handler.OnPress());
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(handler.OnPress());
        Assert.False(handler.OnPress());
    }

    [Fact]
    public void PressAfterTheSourceIsDisposedTerminatesWithoutThrowing()
    {
        var cancellation = new CancellationTokenSource();
        var handler = new CancelKeyHandler(cancellation);
        cancellation.Dispose();

        Assert.False(handler.OnPress());
    }
}
