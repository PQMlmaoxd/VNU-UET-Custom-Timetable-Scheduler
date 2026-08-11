using Scheduler.Desktop;
using Xunit;

namespace Scheduler.Desktop.Tests;

public sealed class DesktopDiagnosticsTests
{
    [Fact]
    public void CreateIncludesStableReleaseMetadataWithoutUserData()
    {
        var diagnostics = DesktopDiagnostics.Create("150.0.4078.99");
        var displayText = diagnostics.ToDisplayText();

        Assert.Equal("cadical-3.0.1", diagnostics.SolverVersion);
        Assert.Equal("c60730422e758ef1cebe7aeddf2dda31c996bf04", diagnostics.CadicalCommit);
        Assert.Equal(DesktopBridgeSession.ProtocolVersion, diagnostics.BridgeProtocolVersion);
        Assert.Equal("150.0.4078.99", diagnostics.WebView2RuntimeVersion);
        Assert.Contains("Activity logs:", displayText, StringComparison.Ordinal);
        Assert.DoesNotContain("workbook", displayText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", displayText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateMarksUnavailableWebViewRuntime()
    {
        var diagnostics = DesktopDiagnostics.Create(null);

        Assert.Equal("not initialized", diagnostics.WebView2RuntimeVersion);
    }
}
