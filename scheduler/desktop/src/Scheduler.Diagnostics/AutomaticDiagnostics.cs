namespace Scheduler.Diagnostics;

internal static class AutomaticDiagnostics
{
    public static DiagnosticsOptions CreateOptions(string executableDirectory) => new(
        DiagnosticsCommand.Doctor,
        Path.Combine(executableDirectory, "SolverWorker.exe"),
        Path.Combine(executableDirectory, "Scheduler.Desktop.exe"),
        null,
        DiagnosticsOutputFormat.Text,
        null,
        IncludePaths: false,
        IncludeFileHashes: false,
        VerbosePrivate: false);
}
