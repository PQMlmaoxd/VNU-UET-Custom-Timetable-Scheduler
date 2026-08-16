using Scheduler.Infrastructure.Pdf;
using Scheduler.Infrastructure.Xlsx;
using Scheduler.Domain;
using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Scheduler.Infrastructure.Pdf.Tests;

public sealed class PdfTimetableParserTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "ExternalFixture")]
    [Trait("Category", "Compatibility")]
    public void ParsesTheSignedTimetableWhenAvailable()
    {
        var path = RequireSignedTimetable();

        var result = PdfTimetableParser.Parse(path, "ALL");

        output.WriteLine($"sessions={result.Problem.Sessions.Length}");
        output.WriteLine($"schedulable={result.Problem.SchedulableSessions.Length}");
        output.WriteLine($"online={result.Problem.OnlineSessions.Length}");
        output.WriteLine($"rooms={result.Problem.AvailableRooms.Length}");
        output.WriteLine($"warnings={result.Warnings.Length}");
        output.WriteLine($"skipped={result.SkippedRows.Length}");
        output.WriteLine($"rows={result.TotalRowsProcessed}");
        foreach (var warning in result.Warnings.Take(20))
        {
            output.WriteLine(warning);
        }
        foreach (var session in result.Problem.Sessions.Take(10))
        {
            output.WriteLine(session.ToString());
        }

        Assert.NotEmpty(result.Problem.Sessions);
    }

    [Fact]
    [Trait("Category", "ExternalFixture")]
    [Trait("Category", "Compatibility")]
    public void MatchesTheWorkbookOnSchedulingSemanticsWhenBothFilesAreAvailable()
    {
        var pdfPath = FindSignedTimetable();
        var workbookPath = FindWorkbook();
        if (pdfPath is null || workbookPath is null)
        {
            throw SkipException.ForSkip("External PDF and XLSX fixtures are required for compatibility parity.");
        }

        var pdfSessions = PdfTimetableParser.Parse(pdfPath).Problem.Sessions
            .Select(CanonicalSession)
            .ToHashSet(StringComparer.Ordinal);
        var workbookSessions = TimetableParser.Parse(workbookPath).Problem.Sessions
            .Select(CanonicalSession)
            .ToHashSet(StringComparer.Ordinal);

        output.WriteLine($"pdf={pdfSessions.Count} workbook={workbookSessions.Count}");
        output.WriteLine($"only-pdf={pdfSessions.Except(workbookSessions).Count()}");
        output.WriteLine($"only-workbook={workbookSessions.Except(pdfSessions).Count()}");
        foreach (var difference in pdfSessions.Except(workbookSessions).Take(10))
        {
            output.WriteLine($"PDF only: {difference}");
        }
        foreach (var difference in workbookSessions.Except(pdfSessions).Take(10))
        {
            output.WriteLine($"XLSX only: {difference}");
        }

        Assert.Equal(workbookSessions, pdfSessions);
    }

    [Fact]
    [Trait("Category", "ExternalFixture")]
    [Trait("Category", "Compatibility")]
    public void MatchesTheWorkbookForCnttScopeWhenBothFilesAreAvailable()
    {
        var pdfPath = FindSignedTimetable();
        var workbookPath = FindWorkbook();
        if (pdfPath is null || workbookPath is null)
        {
            throw SkipException.ForSkip("External PDF and XLSX fixtures are required for CNTT parity.");
        }

        var pdf = PdfTimetableParser.Parse(pdfPath, "CNTT");
        var workbook = TimetableParser.Parse(workbookPath, "CNTT");

        Assert.Equal(
            workbook.Problem.Sessions.Select(CanonicalSession).ToHashSet(StringComparer.Ordinal),
            pdf.Problem.Sessions.Select(CanonicalSession).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void RejectsAFileWithoutPdfSignature()
    {
        var path = Path.Combine(Path.GetTempPath(), $"scheduler-invalid-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, "not a PDF");

        try
        {
            Assert.Throws<InvalidDataException>(() => PdfTimetableParser.Parse(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "ExternalFixture")]
    public void RejectsFilesOutsideConfiguredResourceLimits()
    {
        var path = RequireSignedTimetable();

        Assert.Throws<InvalidDataException>(() => PdfTimetableParser.Parse(
            path,
            options: new PdfTimetableParseOptions { MaxPages = 1 }));

        Assert.Throws<InvalidDataException>(() => PdfTimetableParser.Parse(
            path,
            options: new PdfTimetableParseOptions { MaxWordsPerPage = 1 }));

        Assert.Throws<InvalidDataException>(() => PdfTimetableParser.Parse(
            path,
            options: new PdfTimetableParseOptions { MaxFileBytes = 1024 }));
    }

    [Fact]
    [Trait("Category", "ExternalFixture")]
    public void HonorsCancellationBeforeProcessingPages()
    {
        var path = RequireSignedTimetable();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => PdfTimetableParser.Parse(
            path,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    [Trait("Category", "ExternalFixture")]
    public void ProducesDeterministicSemanticOutput()
    {
        var path = RequireSignedTimetable();

        var first = PdfTimetableParser.Parse(path);
        var second = PdfTimetableParser.Parse(path);

        Assert.Equal(
            first.Problem.Sessions.Select(CanonicalSession),
            second.Problem.Sessions.Select(CanonicalSession));
        Assert.Equal(first.Warnings.ToArray(), second.Warnings.ToArray());
        Assert.Equal(first.SkippedRows.ToArray(), second.SkippedRows.ToArray());
        Assert.Equal(first.TotalRowsProcessed, second.TotalRowsProcessed);
    }

    [Fact]
    [Trait("Category", "ExternalFixture")]
    [Trait("Category", "Performance")]
    public void ParsesReferencePdfWithinDevelopmentBudget()
    {
        var path = RequireSignedTimetable();

        var durations = new List<long>();
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = PdfTimetableParser.Parse(path);
            stopwatch.Stop();
            durations.Add(stopwatch.ElapsedMilliseconds);
            Assert.NotEmpty(result.Problem.Sessions);
        }

        durations.Sort();
        output.WriteLine(
            $"reference-pdf median_ms={durations[1]} max_ms={durations[^1]} " +
            $"working_set_mb={Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024)}");

        // This is intentionally a broad regression budget. The release target is
        // measured separately on supported hardware and is documented in the parser README.
        Assert.True(durations[^1] < 10_000, "Reference PDF parsing exceeded the development budget.");
    }

    private static string? FindSignedTimetable()
    {
        var configured = Environment.GetEnvironmentVariable("SCHEDULER_TEST_PDF");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = current.GetFiles("*.pdf")
                .FirstOrDefault(file => !file.Name.Equals("constraint.pdf", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return candidate.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string RequireSignedTimetable() =>
        FindSignedTimetable()
        ?? throw SkipException.ForSkip("External signed PDF fixture is not available.");

    private static string? FindWorkbook()
    {
        var configured = Environment.GetEnvironmentVariable("SCHEDULER_TEST_WORKBOOK");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = current.GetFiles("*.xlsx")
                .FirstOrDefault(file => file.Name.Contains("THỜI KHÓA BIỂU", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return candidate.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string CanonicalSession(Scheduler.Domain.Session session) => string.Join(
        "|",
        string.Join(",", session.StudentCohorts.Select(cohort => Normalize(cohort.Code)).OrderBy(value => value, StringComparer.Ordinal)),
        Normalize(session.Course.Code),
        Normalize(session.LhpCode),
        Normalize(session.Group),
        Normalize(session.SessionType.ToWorkbookValue()),
        Normalize(session.TimeSlot?.Day.ToString() ?? ""),
        Normalize(session.TimeSlot?.Period.ToWorkbookValue() ?? ""),
        Normalize(session.Room?.Code ?? ""),
        string.Join(",", session.Lecturers.Select(lecturer => Normalize(lecturer.Name)).OrderBy(value => value, StringComparer.Ordinal)));

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormC);
}
