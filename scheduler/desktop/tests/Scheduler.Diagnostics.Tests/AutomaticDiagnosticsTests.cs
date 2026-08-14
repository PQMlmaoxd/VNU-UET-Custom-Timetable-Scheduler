using Xunit;

namespace Scheduler.Diagnostics.Tests;

public sealed class AutomaticDiagnosticsTests
{
    [Fact]
    public void CreatesPrivacySafeDoctorForSiblingTargets()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scheduler diagnostics");

        var options = AutomaticDiagnostics.CreateOptions(directory);

        Assert.Equal(DiagnosticsCommand.Doctor, options.Command);
        Assert.Equal(Path.Combine(directory, "Scheduler.Desktop.exe"), options.AppPath);
        Assert.Equal(Path.Combine(directory, "SolverWorker.exe"), options.WorkerPath);
        Assert.Null(options.WorkbookPath);
        Assert.Equal(DiagnosticsOutputFormat.Text, options.Format);
        Assert.Null(options.OutputPath);
        Assert.False(options.IncludePaths);
        Assert.False(options.IncludeFileHashes);
        Assert.False(options.VerbosePrivate);
    }
}
