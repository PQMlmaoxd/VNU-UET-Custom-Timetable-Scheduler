using Xunit;

namespace Scheduler.Diagnostics.Tests;

public sealed class InteractiveConsoleLifetimeTests
{
    [Theory]
    [InlineData(0, false, false, 1, true)]
    [InlineData(1, false, false, 1, false)]
    [InlineData(0, true, false, 1, false)]
    [InlineData(0, false, true, 1, false)]
    [InlineData(0, false, false, 0, false)]
    [InlineData(0, false, false, 2, false)]
    [InlineData(0, false, false, 3, false)]
    public void PausesOnlyForAnUnredirectedNoArgumentExclusiveConsole(
        int argumentCount,
        bool inputRedirected,
        bool outputRedirected,
        uint attachedConsoleProcessCount,
        bool expected)
    {
        Assert.Equal(expected, InteractiveConsoleLifetime.ShouldPause(
            argumentCount,
            inputRedirected,
            outputRedirected,
            attachedConsoleProcessCount));
    }
}
