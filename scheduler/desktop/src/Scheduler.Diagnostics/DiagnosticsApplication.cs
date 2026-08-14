using System.Text;

namespace Scheduler.Diagnostics;

internal static class DiagnosticsApplication
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        string? executableDirectory = null,
        CancellationToken cancellationToken = default)
    {
        DiagnosticsOptions options;
        try
        {
            options = arguments.Count == 0
                ? AutomaticDiagnostics.CreateOptions(executableDirectory ?? AppContext.BaseDirectory)
                : ArgumentParser.Parse(arguments);
        }
        catch (DiagnosticsUsageException)
        {
            await standardError.WriteLineAsync("Invalid command-line usage. Run 'help' for usage.");
            return (int)DiagnosticsExitCode.Usage;
        }

        try
        {
            if (options.Command == DiagnosticsCommand.Help)
            {
                var help = HelpText();
                if (options.Format == DiagnosticsOutputFormat.Json)
                {
                    var report = DiagnosticsReportFactory.Create(
                        options,
                        "help",
                        [DiagnosticsReportFactory.Check(
                            options,
                            "usage",
                            DiagnosticStatus.Passed,
                            "Help text is available.")],
                        help);
                    return await WriteReportAsync(options, report, standardOutput);
                }

                return await WriteContentAsync(options, help, standardOutput);
            }

            if (options.Command == DiagnosticsCommand.Version)
            {
                if (options.Format == DiagnosticsOutputFormat.Json)
                {
                    var report = DiagnosticsReportFactory.Create(
                        options,
                        "version",
                        [DiagnosticsReportFactory.Check(
                            options,
                            "version",
                            DiagnosticStatus.Passed,
                            "Diagnostics CLI version is available.")]);
                    return await WriteReportAsync(options, report, standardOutput);
                }

                return await WriteContentAsync(
                    options,
                    $"{DiagnosticsReportFactory.ToolName} {DiagnosticsReportFactory.ToolVersion}{Environment.NewLine}",
                    standardOutput);
            }

            var result = await DiagnosticsRunner.RunAsync(options, cancellationToken);
            return await WriteReportAsync(options, result, standardOutput);
        }
        catch (DiagnosticsUsageException)
        {
            await standardError.WriteLineAsync("Invalid command-line usage. Run 'help' for usage.");
            return (int)DiagnosticsExitCode.Usage;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Diagnostics were cancelled.");
            return (int)DiagnosticsExitCode.Internal;
        }
        catch (Exception exception)
        {
            var detail = options.VerbosePrivate ? DiagnosticsReportFactory.ExceptionDetail(exception) : null;
            var report = DiagnosticsReportFactory.Create(
                options,
                CommandName(options.Command),
                [DiagnosticsReportFactory.Check(
                    options,
                    "internal",
                    DiagnosticStatus.Internal,
                    "The diagnostic command could not complete.",
                    detail: detail)]);
            return await WriteReportAsync(options, report, standardOutput);
        }
    }

    private static async Task<int> WriteReportAsync(
        DiagnosticsOptions options,
        DiagnosticReport report,
        TextWriter standardOutput)
    {
        var content = options.Format == DiagnosticsOutputFormat.Json
            ? DiagnosticsReportFactory.SerializeJson(report) + Environment.NewLine
            : DiagnosticsReportFactory.ToText(report);
        return await WriteContentAsync(options, content, standardOutput, report.ExitCode);
    }

    private static async Task<int> WriteContentAsync(
        DiagnosticsOptions options,
        string content,
        TextWriter standardOutput,
        int exitCode = 0)
    {
        try
        {
            if (options.OutputPath is not null)
            {
                var outputPath = Path.GetFullPath(options.OutputPath);
                var directory = Path.GetDirectoryName(outputPath);
                if (directory is not null)
                {
                    Directory.CreateDirectory(directory);
                }

                await File.WriteAllTextAsync(outputPath, content, new UTF8Encoding(false));
            }

            await standardOutput.WriteAsync(content);
            if (!content.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                await standardOutput.WriteLineAsync();
            }

            return exitCode;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await standardOutput.WriteLineAsync("Diagnostics report could not be written.");
            return (int)DiagnosticsExitCode.Internal;
        }
    }

    private static string CommandName(DiagnosticsCommand command) => command switch
    {
        DiagnosticsCommand.SelfTest => "self-test",
        DiagnosticsCommand.Worker => "worker",
        DiagnosticsCommand.App => "app",
        DiagnosticsCommand.Workbook => "workbook",
        DiagnosticsCommand.Doctor => "doctor",
        _ => command.ToString().ToLowerInvariant(),
    };

    private static string HelpText() => """
Scheduler diagnostics CLI

Commands:
  help
  version
  self-test
  worker --worker <path>
  app --app <directory-or-exe>
  workbook --workbook <xlsx-or-pdf>
  doctor [--app <directory-or-exe>] [--worker <path>] [--workbook <xlsx-or-pdf>]

Options:
  --format text|json       Select output format (default: text).
  --output <file>         Also write the report to a file.
  --include-paths         Include target paths in the report.
  --include-file-hashes   Include SHA-256 values in the report.
  --verbose-private       Include bounded private diagnostic details.

Exit codes: 0 pass, 1 failed checks, 2 usage, 4 missing target, 5 internal error.
The default report omits machine, user, environment, absolute path, and input-content details.
With no arguments, the CLI checks sibling Scheduler.Desktop.exe and SolverWorker.exe.
It does not discover workbooks. An Explorer-owned console waits for a key after the report.
""";
}
