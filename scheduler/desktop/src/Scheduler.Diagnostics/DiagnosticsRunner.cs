namespace Scheduler.Diagnostics;

internal static class DiagnosticsRunner
{
    public static async Task<DiagnosticReport> RunAsync(
        DiagnosticsOptions options,
        CancellationToken cancellationToken = default)
    {
        return options.Command switch
        {
            DiagnosticsCommand.SelfTest => SelfTestDiagnostics.Run(options),
            DiagnosticsCommand.Worker => await RunWorkerAsync(options, cancellationToken),
            DiagnosticsCommand.App => RunApp(options),
            DiagnosticsCommand.Workbook => RunWorkbook(options),
            DiagnosticsCommand.Doctor => await RunDoctorAsync(options, cancellationToken),
            _ => throw new DiagnosticsUsageException(),
        };
    }

    private static async Task<DiagnosticReport> RunWorkerAsync(
        DiagnosticsOptions options,
        CancellationToken cancellationToken)
    {
        var path = ResolveTarget(options.WorkerPath!);
        if (path is null || !File.Exists(path))
        {
            return DiagnosticsReportFactory.Create(
                options,
                "worker",
                [DiagnosticsReportFactory.Check(
                    options,
                    "worker_target",
                    DiagnosticStatus.Missing,
                    "Worker target was not found.",
                    path: options.WorkerPath)]);
        }

        return await WorkerDiagnostics.RunAsync(options, path, cancellationToken);
    }

    private static DiagnosticReport RunApp(DiagnosticsOptions options)
    {
        var path = ResolveTarget(options.AppPath!);
        if (path is null || (!Directory.Exists(path) && !File.Exists(path)))
        {
            return DiagnosticsReportFactory.Create(
                options,
                "app",
                [DiagnosticsReportFactory.Check(
                    options,
                    "app_target",
                    DiagnosticStatus.Missing,
                    "Application target was not found.",
                    path: options.AppPath)]);
        }

        if (File.Exists(path) && !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticsReportFactory.Create(
                options,
                "app",
                [DiagnosticsReportFactory.Check(
                    options,
                    "app_target",
                    DiagnosticStatus.Failed,
                    "Application target must be a package directory or an executable.",
                    path: path)]);
        }

        return AppDiagnostics.Run(options, path);
    }

    private static DiagnosticReport RunWorkbook(DiagnosticsOptions options)
    {
        var path = ResolveTarget(options.WorkbookPath!);
        if (path is null || !File.Exists(path))
        {
            return DiagnosticsReportFactory.Create(
                options,
                "workbook",
                [DiagnosticsReportFactory.Check(
                    options,
                    "workbook_target",
                    DiagnosticStatus.Missing,
                    "Workbook target was not found.",
                    path: options.WorkbookPath)]);
        }

        return WorkbookDiagnostics.Run(options, path);
    }

    private static async Task<DiagnosticReport> RunDoctorAsync(
        DiagnosticsOptions options,
        CancellationToken cancellationToken)
    {
        var checks = new List<DiagnosticCheck>();
        var selfTest = SelfTestDiagnostics.Run(options);
        checks.AddRange(selfTest.Checks);

        if (options.AppPath is not null)
        {
            var app = RunApp(options);
            checks.AddRange(PrefixChecks("app", app.Checks));
        }

        if (options.WorkerPath is not null)
        {
            var worker = await RunWorkerAsync(options, cancellationToken);
            checks.AddRange(PrefixChecks("worker", worker.Checks));
        }

        if (options.WorkbookPath is not null)
        {
            var workbook = RunWorkbook(options);
            checks.AddRange(PrefixChecks("workbook", workbook.Checks));
        }

        return DiagnosticsReportFactory.Create(options, "doctor", checks);
    }

    private static IEnumerable<DiagnosticCheck> PrefixChecks(
        string prefix,
        IEnumerable<DiagnosticCheck> checks) => checks.Select(check => check with
        {
            Name = prefix + "." + check.Name,
        });

    private static string? ResolveTarget(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
