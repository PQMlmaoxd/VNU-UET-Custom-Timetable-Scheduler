using Xunit;

namespace Scheduler.Diagnostics.Tests;

public sealed class WorkerDiagnosticsTests
{
    [RequiresNativeWorkerFact]
    [Trait("Category", "NativeIntegration")]
    public async Task RunsBoundedKnownSatAndUnsatChecksAgainstProvidedWorker()
    {
        var workerPath = Environment.GetEnvironmentVariable("SCHEDULER_SOLVER_WORKER");
        Assert.False(string.IsNullOrWhiteSpace(workerPath));
        Assert.True(File.Exists(workerPath));

        var options = new DiagnosticsOptions(
            DiagnosticsCommand.Worker,
            workerPath,
            null,
            null,
            DiagnosticsOutputFormat.Json,
            null,
            false,
            false,
            false);
        var report = await WorkerDiagnostics.RunAsync(options, workerPath, CancellationToken.None);

        Assert.Equal(DiagnosticStatus.Passed, report.Status);
        Assert.Contains(report.Checks, check => check.Name == "known_sat" && check.Status == DiagnosticStatus.Passed);
        Assert.Contains(report.Checks, check => check.Name == "known_unsat" && check.Status == DiagnosticStatus.Passed);
    }
}

internal sealed class RequiresNativeWorkerFactAttribute : FactAttribute
{
    public RequiresNativeWorkerFactAttribute()
    {
        var workerPath = Environment.GetEnvironmentVariable("SCHEDULER_SOLVER_WORKER");
        if (string.IsNullOrWhiteSpace(workerPath) || !File.Exists(workerPath))
        {
            Skip = "Set SCHEDULER_SOLVER_WORKER to a built SolverWorker executable.";
        }
    }
}
