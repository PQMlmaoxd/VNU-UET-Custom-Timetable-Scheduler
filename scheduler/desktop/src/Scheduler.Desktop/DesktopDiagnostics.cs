using System.Globalization;
using System.Text;

namespace Scheduler.Desktop;

/// <summary>
/// Local, payload-free information for support and release verification.
/// </summary>
public sealed record DesktopDiagnostics(
    string ApplicationVersion,
    string SolverVersion,
    string CadicalCommit,
    int BridgeProtocolVersion,
    string WebView2RuntimeVersion,
    string ActivityLogDirectory)
{
    public static DesktopDiagnostics Create(string? webView2RuntimeVersion) => new(
        DesktopReleaseMetadata.ApplicationVersion,
        DesktopReleaseMetadata.SolverVersion,
        DesktopReleaseMetadata.CadicalCommit,
        DesktopBridgeSession.ProtocolVersion,
        string.IsNullOrWhiteSpace(webView2RuntimeVersion) ? "not initialized" : webView2RuntimeVersion,
        DesktopReleaseMetadata.DefaultLogDirectory);

    public string ToDisplayText()
    {
        var text = new StringBuilder();
        text.AppendLine("VNU-UET Custom Timetable Scheduler diagnostics");
        text.AppendLine();
        text.AppendLine(string.Concat("Application version: ", ApplicationVersion));
        text.AppendLine(string.Concat("Solver: ", SolverVersion));
        text.AppendLine(string.Concat("CaDiCaL commit: ", CadicalCommit));
        text.AppendLine(string.Concat(
            "Bridge protocol: ",
            BridgeProtocolVersion.ToString(CultureInfo.InvariantCulture)));
        text.AppendLine(string.Concat("WebView2 runtime: ", WebView2RuntimeVersion));
        text.Append(string.Concat("Activity logs: ", ActivityLogDirectory));
        return text.ToString();
    }
}
