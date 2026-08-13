namespace Scheduler.Diagnostics;

internal enum DiagnosticsCommand
{
    Help,
    Version,
    SelfTest,
    Worker,
    App,
    Workbook,
    Doctor,
}

internal enum DiagnosticsOutputFormat
{
    Text,
    Json,
}

internal sealed record DiagnosticsOptions(
    DiagnosticsCommand Command,
    string? WorkerPath,
    string? AppPath,
    string? WorkbookPath,
    DiagnosticsOutputFormat Format,
    string? OutputPath,
    bool IncludePaths,
    bool IncludeFileHashes,
    bool VerbosePrivate);

internal sealed class DiagnosticsUsageException : Exception
{
    public DiagnosticsUsageException()
        : base("Invalid diagnostic command-line usage.")
    {
    }
}
