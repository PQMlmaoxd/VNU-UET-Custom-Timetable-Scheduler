using Scheduler.Desktop;
using Xunit;

namespace Scheduler.Desktop.Tests;

public sealed class DesktopThemeTests
{
    [Theory]
    [InlineData("system", DesktopThemePreference.System)]
    [InlineData("light", DesktopThemePreference.Light)]
    [InlineData("dark", DesktopThemePreference.Dark)]
    [InlineData("unknown", DesktopThemePreference.System)]
    public void ParsePreferenceUsesSystemForInvalidValues(string value, DesktopThemePreference expected)
    {
        Assert.Equal(expected, DesktopTheme.ParsePreference(value));
    }

    [Fact]
    public void PaletteForDarkUsesAReadableMidnightSurface()
    {
        var palette = DesktopTheme.PaletteFor(DesktopThemePreference.Dark);

        Assert.Equal(0x0B, palette.Canvas.R);
        Assert.Equal(0x12, palette.Canvas.G);
        Assert.Equal(0x20, palette.Canvas.B);
        Assert.True(palette.Text.R > palette.Canvas.R);
    }

    [Fact]
    public void BootstrapScriptStoresThePreferenceBeforeReactLoads()
    {
        var script = DesktopTheme.CreateDocumentBootstrapScript(DesktopThemePreference.Dark);

        Assert.Contains("scheduler.theme", script, StringComparison.Ordinal);
        Assert.Contains("\"dark\"", script, StringComparison.Ordinal);
        Assert.Contains("document.documentElement.dataset.theme", script, StringComparison.Ordinal);
    }
}
