using Scheduler.Infrastructure.Xlsx;
using Scheduler.Infrastructure.Pdf;

namespace Scheduler.Diagnostics;

internal static class WorkbookDiagnostics
{
    public static DiagnosticReport Run(DiagnosticsOptions options, string workbookPath)
    {
        string? fileHash = null;
        string? fileHashError = null;
        if (options.IncludeFileHashes)
        {
            fileHash = DiagnosticsReportFactory.TryComputeSha256(workbookPath, out fileHashError);
        }
        var targetCheck = DiagnosticsReportFactory.Check(
            options,
            "workbook_target",
            options.IncludeFileHashes && fileHash is null ? DiagnosticStatus.Failed : DiagnosticStatus.Passed,
            options.IncludeFileHashes && fileHash is null
                ? "Workbook target is present, but its requested hash could not be computed."
                : "Workbook target is present.",
            path: workbookPath,
            fileHashes: options.IncludeFileHashes
                ? [new DiagnosticFileHash("workbook", null, fileHash, fileHash is null ? "unavailable" : "computed")]
                : null,
            detail: fileHashError);
        var checks = new List<DiagnosticCheck> { targetCheck };
        var extension = Path.GetExtension(workbookPath);
        if (!string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            checks.Add(DiagnosticsReportFactory.Check(
                options,
                "workbook_parser",
                DiagnosticStatus.Unsupported,
                "This workbook format is unsupported; use an XLSX input."));
            return DiagnosticsReportFactory.Create(options, "workbook", checks);
        }

        var formatName = string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "XLSX";
        try
        {
            var result = string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)
                ? PdfTimetableParser.Parse(workbookPath, "ALL")
                : TimetableParser.Parse(workbookPath, "ALL");
            var metrics = new Dictionary<string, long>
            {
                ["rows_processed"] = result.TotalRowsProcessed,
                ["sessions"] = result.Problem.Sessions.Length,
                ["schedulable_sessions"] = result.Problem.SchedulableSessions.Length,
                ["online_sessions"] = result.Problem.OnlineSessions.Length,
                ["other_scope_sessions"] = result.OtherDepartmentSessions.Length,
                ["rooms"] = result.Problem.AvailableRooms.Length,
                ["lecturer_blocks"] = result.Problem.LecturerBlocks.Length,
                ["skipped_rows"] = result.SkippedRows.Length,
                ["warnings"] = result.Warnings.Length,
                ["fatal_warnings"] = result.FatalWarnings.Length,
                ["quarantined_offerings"] = result.QuarantinedOfferings.Length,
            };
            var parserStatus = result.FatalWarnings.IsEmpty
                ? DiagnosticStatus.Passed
                : DiagnosticStatus.Failed;
            var detail = result.Warnings.IsEmpty
                ? null
                : string.Join(" | ", result.Warnings);
            checks.Add(DiagnosticsReportFactory.Check(
                options,
                "workbook_parser",
                parserStatus,
                parserStatus == DiagnosticStatus.Passed
                    ? result.Warnings.IsEmpty
                        ? $"{formatName} parser accepted the workbook."
                        : $"{formatName} parser accepted the workbook with non-blocking warnings."
                    : $"{formatName} parser found blocking warnings.",
                metrics,
                detail: detail));
        }
        catch (Exception exception)
        {
            checks.Add(DiagnosticsReportFactory.Check(
                options,
                "workbook_parser",
                DiagnosticStatus.Failed,
                $"{formatName} parser could not accept the workbook.",
                detail: DiagnosticsReportFactory.ExceptionDetail(exception)));
        }

        return DiagnosticsReportFactory.Create(options, "workbook", checks);
    }
}
