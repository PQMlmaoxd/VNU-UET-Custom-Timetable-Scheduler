using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;

namespace Scheduler.Diagnostics.Tests;

public sealed class XlsxDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "scheduler-diagnostics-xlsx-" + Guid.NewGuid().ToString("N"));

    public XlsxDiagnosticsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void UsesExistingXlsxParserAndReturnsPrivacySafeSummaryMetrics()
    {
        var path = Path.Combine(_directory, "private-course-lecturer.xlsx");
        CreateWorkbook(path);
        var options = new DiagnosticsOptions(
            DiagnosticsCommand.Workbook,
            null,
            null,
            path,
            DiagnosticsOutputFormat.Json,
            null,
            IncludePaths: false,
            IncludeFileHashes: false,
            VerbosePrivate: false);

        var report = WorkbookDiagnostics.Run(options, path);
        var parserCheck = Assert.Single(report.Checks, check => check.Name == "workbook_parser");

        Assert.Equal(DiagnosticStatus.Passed, report.Status);
        Assert.Equal(DiagnosticStatus.Passed, parserCheck.Status);
        Assert.Equal(1, parserCheck.Metrics!["sessions"]);
        Assert.Equal(1, parserCheck.Metrics["schedulable_sessions"]);
        Assert.DoesNotContain("private-course-lecturer.xlsx", DiagnosticsReportFactory.SerializeJson(report), StringComparison.Ordinal);
        Assert.DoesNotContain("Cohort", DiagnosticsReportFactory.SerializeJson(report), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void CreateWorkbook(string path)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData(
            CreateRow(5, new Dictionary<uint, string?>
            {
                [1] = "K69I-IT1",
                [2] = "INT1000",
                [3] = "Programming",
                [4] = "3",
                [9] = "30",
                [10] = "LHP-1",
                [11] = "CL",
                [12] = "LT",
                [13] = "2",
                [14] = "1",
                [15] = "101-A",
                [16] = "Alice",
            })));
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Sheet3",
        });
        workbookPart.Workbook.Save();
    }

    private static Row CreateRow(uint index, IReadOnlyDictionary<uint, string?> values)
    {
        var row = new Row { RowIndex = index };
        foreach (var (column, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            row.Append(new Cell
            {
                CellReference = ColumnName(column) + index,
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text(value)),
            });
        }

        return row;
    }

    private static string ColumnName(uint column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + (column % 26)) + result;
            column /= 26;
        }

        return result;
    }
}
