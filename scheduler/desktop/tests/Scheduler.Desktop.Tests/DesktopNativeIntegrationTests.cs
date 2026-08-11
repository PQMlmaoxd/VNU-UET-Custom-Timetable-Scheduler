using System.Text.Json;
using Scheduler.Desktop;
using Xunit;
using Xunit.Sdk;

namespace Scheduler.Desktop.Tests;

/// <summary>
/// Runs the complete local desktop command path when release inputs are available:
/// XLSX upload bytes, OpenXML import, CNF creation, native process, validation, and UI DTO mapping.
/// </summary>
public sealed class DesktopNativeIntegrationTests
{
    private const string WorkerEnvironmentVariable = "SCHEDULER_SOLVER_WORKER";

    [Fact]
    [Trait("Category", "NativeIntegration")]
    [Trait("Category", "Compatibility")]
    public async Task SolveWorkbookReturnsValidatedPersonalSelectionFromNativeWorker()
    {
        var workbookPath = FindWorkbook();
        var workerPath = Environment.GetEnvironmentVariable(WorkerEnvironmentVariable);
        if (workbookPath is null || string.IsNullOrWhiteSpace(workerPath) || !File.Exists(workerPath))
        {
            throw SkipException.ForSkip(
                $"Provide an external XLSX fixture and {WorkerEnvironmentVariable} to run desktop native integration tests.");
        }

        var workbookName = Path.GetFileName(workbookPath);

        var workbook = new
        {
            file_name = workbookName,
            bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
        };
        var dispatcher = new DesktopCommandDispatcher();
        var validation = await dispatcher.DispatchAsync(
            "validate_workbook",
            JsonSerializer.SerializeToElement(new { workbook }),
            CancellationToken.None);
        var desiredAssignment = PickSingleTeachingTeamAnchor(validation);

        var result = await dispatcher.DispatchAsync(
            "solve_workbook",
            JsonSerializer.SerializeToElement(new
            {
                workbook,
                desired_assignments = new[]
                {
                    new
                    {
                        course_code = desiredAssignment.CourseCode,
                        course_name = desiredAssignment.CourseName,
                        teaching_team_key = desiredAssignment.TeachingTeamKey,
                        teaching_team_label = desiredAssignment.TeachingTeamLabel,
                    },
                },
                timeout_seconds = 60,
            }),
            CancellationToken.None);

        Assert.Equal("reschedule", result.GetProperty("mode").GetString());
        Assert.Equal(1, result.GetProperty("parse_summary").GetProperty("requested_assignments").GetInt32());
        Assert.Equal("cadical", result.GetProperty("solver").GetProperty("backend").GetString());
        Assert.Equal("SAT", result.GetProperty("solver").GetProperty("satisfiability").GetString());
        Assert.True(result.GetProperty("solver").GetProperty("solution_count").GetInt32() >= 1);

        var selectedAssignment = result.GetProperty("desired_assignments")[0];
        Assert.Equal(desiredAssignment.CourseCode, selectedAssignment.GetProperty("course_code").GetString());
        Assert.Equal(desiredAssignment.TeachingTeamKey, selectedAssignment.GetProperty("teaching_team_key").GetString());
        Assert.Equal(desiredAssignment.TeachingTeamLabel, selectedAssignment.GetProperty("teaching_team_label").GetString());
        Assert.True(selectedAssignment.GetProperty("matched_sessions").GetArrayLength() >= 1);
        Assert.True(result.GetProperty("solutions")[0].GetProperty("desired_assignments")[0]
            .GetProperty("matched_sessions").GetArrayLength() >= 1);
    }

    [Fact]
    [Trait("Category", "NativeIntegration")]
    [Trait("Category", "Compatibility")]
    public async Task SolveSignedPdfReturnsValidatedPersonalSelectionFromNativeWorker()
    {
        var pdfPath = FindPdf();
        var workerPath = Environment.GetEnvironmentVariable(WorkerEnvironmentVariable);
        if (pdfPath is null || string.IsNullOrWhiteSpace(workerPath) || !File.Exists(workerPath))
        {
            throw SkipException.ForSkip(
                $"Provide an external signed PDF fixture and {WorkerEnvironmentVariable} to run PDF desktop native integration tests.");
        }

        var pdfName = Path.GetFileName(pdfPath);

        var document = new
        {
            file_name = pdfName,
            bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(pdfPath)),
        };
        var dispatcher = new DesktopCommandDispatcher();
        var validation = await dispatcher.DispatchAsync(
            "validate_workbook",
            JsonSerializer.SerializeToElement(new { workbook = document }),
            CancellationToken.None);
        var desiredAssignment = PickSingleTeachingTeamAnchor(validation);

        var result = await dispatcher.DispatchAsync(
            "solve_workbook",
            JsonSerializer.SerializeToElement(new
            {
                workbook = document,
                desired_assignments = new[]
                {
                    new
                    {
                        course_code = desiredAssignment.CourseCode,
                        course_name = desiredAssignment.CourseName,
                        teaching_team_key = desiredAssignment.TeachingTeamKey,
                        teaching_team_label = desiredAssignment.TeachingTeamLabel,
                    },
                },
                timeout_seconds = 60,
            }),
            CancellationToken.None);

        Assert.Equal("reschedule", result.GetProperty("mode").GetString());
        Assert.Equal("SAT", result.GetProperty("solver").GetProperty("satisfiability").GetString());
        Assert.True(result.GetProperty("solutions")[0].GetProperty("desired_assignments")[0]
            .GetProperty("matched_sessions").GetArrayLength() >= 1);
    }

    private static DesiredAssignment PickSingleTeachingTeamAnchor(JsonElement validation)
    {
        var anchors = validation.GetProperty("prototype_catalog").GetProperty("anchors")
            .EnumerateArray()
            .Select(anchor => new DesiredAssignment(
                anchor.GetProperty("course_code").GetString() ?? string.Empty,
                anchor.GetProperty("course_name").GetString() ?? string.Empty,
                anchor.GetProperty("teaching_team_key").GetString() ?? string.Empty,
                anchor.GetProperty("teaching_team_label").GetString() ?? string.Empty))
            .ToArray();

        return anchors
            .GroupBy(anchor => anchor.CourseCode, StringComparer.Ordinal)
            .Select(group => group.ToArray())
            .FirstOrDefault(group => group.Select(anchor => anchor.TeachingTeamKey).Distinct(StringComparer.Ordinal).Count() == 1)
            ?.FirstOrDefault()
            ?? throw new InvalidOperationException("The workbook has no course with exactly one selectable teaching team.");
    }

    private static string? FindWorkbook()
    {
        var configured = Environment.GetEnvironmentVariable("SCHEDULER_TEST_WORKBOOK");
        return !string.IsNullOrWhiteSpace(configured)
            ? (File.Exists(configured) ? Path.GetFullPath(configured) : null)
            : FindFile("*.xlsx");
    }

    private static string? FindPdf()
    {
        var configured = Environment.GetEnvironmentVariable("SCHEDULER_TEST_PDF");
        return !string.IsNullOrWhiteSpace(configured)
            ? (File.Exists(configured) ? Path.GetFullPath(configured) : null)
            : FindFile("*.pdf", excludeName: "constraint.pdf");
    }

    private static string? FindFile(string pattern, string? excludeName = null)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = directory.GetFiles(pattern)
                .FirstOrDefault(file => excludeName is null ||
                    !file.Name.Equals(excludeName, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    private sealed record DesiredAssignment(
        string CourseCode,
        string CourseName,
        string TeachingTeamKey,
        string TeachingTeamLabel);
}
