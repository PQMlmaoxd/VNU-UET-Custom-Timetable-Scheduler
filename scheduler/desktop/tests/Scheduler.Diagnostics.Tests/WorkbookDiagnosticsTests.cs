using Xunit;

namespace Scheduler.Diagnostics.Tests;

public sealed class WorkbookDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "scheduler-diagnostics-workbook-" + Guid.NewGuid().ToString("N"));

    public WorkbookDiagnosticsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ReportsInvalidPdfAsParserFailure()
    {
        var path = Path.Combine(_directory, "input.pdf");
        File.WriteAllText(path, "%PDF-unsupported-test");
        var options = new DiagnosticsOptions(
            DiagnosticsCommand.Workbook,
            null,
            null,
            path,
            DiagnosticsOutputFormat.Text,
            null,
            false,
            false,
            false);

        var report = WorkbookDiagnostics.Run(options, path);

        Assert.Equal(DiagnosticStatus.Failed, report.Status);
        var parserCheck = Assert.Single(report.Checks, check => check.Name == "workbook_parser");
        Assert.Equal(DiagnosticStatus.Failed, parserCheck.Status);
        Assert.Equal((int)DiagnosticsExitCode.FailedChecks, report.ExitCode);
        Assert.DoesNotContain(path, DiagnosticsReportFactory.SerializeJson(report), StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
