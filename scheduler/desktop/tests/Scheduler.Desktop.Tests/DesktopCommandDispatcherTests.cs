using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Scheduler.Desktop;
using Scheduler.Application;
using Xunit;
using Xunit.Sdk;

namespace Scheduler.Desktop.Tests;

public sealed class DesktopCommandDispatcherTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public DesktopCommandDispatcherTests() => Directory.CreateDirectory(temporaryDirectory);

    [Fact]
    public async Task ValidateWorkbookReturnsCurrentReactContractForImportedWorkbook()
    {
        var workbookPath = CreateWorkbook();
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
        });

        var result = await new DesktopCommandDispatcher().DispatchAsync(
            "validate_workbook",
            payload,
            CancellationToken.None);

        Assert.Equal("validate_existing", result.GetProperty("mode").GetString());
        Assert.Equal("fixture.xlsx", result.GetProperty("workbook_path").GetString());
        Assert.Equal(1, result.GetProperty("parse_summary").GetProperty("sessions").GetInt32());
        Assert.Equal(1, result.GetProperty("parse_summary").GetProperty("anchor_count").GetInt32());
        Assert.True(result.GetProperty("existing_schedule_validation").GetProperty("is_valid").GetBoolean());
        Assert.Equal("INT1000", result.GetProperty("prototype_catalog").GetProperty("anchors")[0]
            .GetProperty("course_code").GetString());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("parse_summary").GetProperty("warnings").ValueKind);
        Assert.False(result.GetProperty("parse_summary").GetProperty("partial_import").GetBoolean());
        Assert.Equal(0, result.GetProperty("parse_summary").GetProperty("quarantined_lhp_count").GetInt32());
        Assert.Equal(JsonValueKind.Array, result.GetProperty("parse_summary")
            .GetProperty("quarantined_offerings").ValueKind);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("existing_schedule_validation")
            .GetProperty("sample_violations").ValueKind);
        Assert.Equal(JsonValueKind.Number, result.GetProperty("prototype_catalog").GetProperty("room_cost_rules")[0]
            .GetProperty("cost").ValueKind);
    }

    [Fact]
    [Trait("Category", "Compatibility")]
    public async Task ValidatePdfReturnsCurrentReactContractWhenSignedPdfIsAvailable()
    {
        var pdfPath = FindSignedPdf();
        if (pdfPath is null)
        {
            throw SkipException.ForSkip("External signed PDF fixture is not available.");
        }

        var pdfName = Path.GetFileName(pdfPath);

        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = pdfName,
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(pdfPath)),
            },
        });

        var result = await new DesktopCommandDispatcher().DispatchAsync(
            "validate_workbook",
            payload,
            CancellationToken.None);

        Assert.Equal("validate_existing", result.GetProperty("mode").GetString());
        Assert.Equal(pdfName, result.GetProperty("workbook_path").GetString());
        Assert.Equal(1661, result.GetProperty("parse_summary").GetProperty("sessions").GetInt32());
        Assert.Equal(50, result.GetProperty("parse_summary").GetProperty("online_sessions").GetInt32());
    }

    [Fact]
    public async Task SolveWorkbookReturnsCurrentReactContractForValidatedModel()
    {
        var workbookPath = CreateWorkbook();
        var solvePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new
                {
                    course_code = "INT1000",
                    course_name = "Programming",
                    teaching_team_label = "Alice",
                },
            },
            timeout_seconds = 30,
        });

        var result = await new DesktopCommandDispatcher(static () => new SatisfyingSolver()).DispatchAsync(
            "solve_workbook",
            solvePayload,
            CancellationToken.None);

        Assert.Equal("reschedule", result.GetProperty("mode").GetString());
        Assert.Equal("cadical", result.GetProperty("solver").GetProperty("backend").GetString());
        Assert.Equal("SAT", result.GetProperty("solver").GetProperty("satisfiability").GetString());
        Assert.Equal(1, result.GetProperty("solver").GetProperty("assignment_count").GetInt32());
        Assert.Null(result.GetProperty("solver").GetProperty("formal_verification_token").GetString());
        Assert.Equal(1, result.GetProperty("solutions").GetArrayLength());
        Assert.Equal(0, result.GetProperty("solutions")[0].GetProperty("movement_cost").GetInt32());
        Assert.Equal("LHP-1", result.GetProperty("solutions")[0].GetProperty("desired_assignments")[0]
            .GetProperty("lhp_codes")[0].GetString());
    }

    [Fact]
    public async Task SolveWorkbookRejectsFatalScheduleGapBeforeStartingSolver()
    {
        var workbookPath = CreateWorkbook(includeRoom: false, includeValidSession: true);
        var solvePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new
                {
                    course_code = "INT1000",
                    course_name = "Programming",
                    teaching_team_label = "Alice",
                },
            },
            timeout_seconds = 30,
        });
        var solverCalled = false;
        var dispatcher = new DesktopCommandDispatcher(() =>
        {
            solverCalled = true;
            return new SatisfyingSolver();
        });

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "solve_workbook",
            solvePayload,
            CancellationToken.None));

        Assert.False(solverCalled);
        Assert.Contains("thiếu hoặc sai thông tin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportUnsatArtifactWritesACompleteCnfPackageWithoutChangingSolverFlow()
    {
        var workbookPath = CreateWorkbook();
        var destination = Path.Combine(temporaryDirectory, "unsat-verification.zip");
        var solvePayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            timeout_seconds = 30,
        });

        var dispatcher = new DesktopCommandDispatcher(
            static () => new InfeasibleSolver(),
            () => destination);
        var result = await dispatcher.DispatchAsync(
            "solve_workbook",
            solvePayload,
            CancellationToken.None);

        var token = result.GetProperty("solver").GetProperty("formal_verification_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var exportPayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            verification_token = token,
        });

        result = await dispatcher.DispatchAsync(
            "export_unsat_artifact",
            exportPayload,
            CancellationToken.None);

        Assert.True(result.GetProperty("exported").GetBoolean());
        Assert.Equal("unsat-verification.zip", result.GetProperty("file_name").GetString());
        Assert.Equal(1, result.GetProperty("variable_count").GetInt32());
        Assert.Equal(1, result.GetProperty("clause_count").GetInt32());
        Assert.True(File.Exists(destination));

        using var archive = ZipFile.OpenRead(destination);
        Assert.Contains(archive.Entries, entry => entry.FullName == "formula.cnf");
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "verify-unsat.ps1");
        var manifest = archive.GetEntry("manifest.json");
        Assert.NotNull(manifest);
        using var manifestReader = new StreamReader(manifest!.Open());
        using var document = JsonDocument.Parse(manifestReader.ReadToEnd());
        Assert.Equal("INT1000", document.RootElement.GetProperty("selection").GetProperty("desired_assignments")[0]
            .GetProperty("course_code").GetString());
    }

    [Fact]
    public async Task PartialImportKeepsCompleteOfferingsButDoesNotIssueFormalUnsatToken()
    {
        var workbookPath = CreateWorkbook(includeUnresolvedSession: true);
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            timeout_seconds = 30,
        });
        var destination = Path.Combine(temporaryDirectory, "partial-unsat.zip");
        var dispatcher = new DesktopCommandDispatcher(
            static () => new InfeasibleSolver(),
            () => destination);

        var result = await dispatcher.DispatchAsync("solve_workbook", payload, CancellationToken.None);

        var parseSummary = result.GetProperty("parse_summary");
        Assert.True(parseSummary.GetProperty("partial_import").GetBoolean());
        Assert.Equal(1, parseSummary.GetProperty("quarantined_lhp_count").GetInt32());
        Assert.Equal("INT1000", parseSummary.GetProperty("quarantined_offerings")[0]
            .GetProperty("course_code").GetString());
        Assert.Equal("LHP-2", parseSummary.GetProperty("quarantined_offerings")[0]
            .GetProperty("lhp_code").GetString());
        Assert.Null(result.GetProperty("solver").GetProperty("formal_verification_token").GetString());

        var exportPayload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            verification_token = "stale-token",
        });

        var exception = await Assert.ThrowsAsync<DesktopBridgeException>(() => dispatcher.DispatchAsync(
            "export_unsat_artifact",
            exportPayload,
            CancellationToken.None));

        Assert.Contains("chưa công bố lịch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SolveWorkbookForwardsCancellationToTheSolver()
    {
        var workbookPath = CreateWorkbook();
        var payload = JsonSerializer.SerializeToElement(new
        {
            workbook = new
            {
                file_name = "fixture.xlsx",
                bytes_base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(workbookPath)),
            },
            desired_assignments = new[]
            {
                new { course_code = "INT1000", course_name = "Programming", teaching_team_label = "Alice" },
            },
            timeout_seconds = 30,
        });
        var solver = new BlockingSolver();
        using var cancellation = new CancellationTokenSource();

        var solve = new DesktopCommandDispatcher(() => solver).DispatchAsync(
            "solve_workbook",
            payload,
            cancellation.Token);
        await solver.Started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => solve);
    }

    public void Dispose() => Directory.Delete(temporaryDirectory, true);

    private string CreateWorkbook(
        bool includeRoom = true,
        bool includeValidSession = false,
        bool includeUnresolvedSession = false)
    {
        var path = Path.Combine(temporaryDirectory, "fixture.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        var row = new Row { RowIndex = 5 };
        AppendCell(row, "A5", "K69I-IT1");
        AppendCell(row, "B5", "INT1000");
        AppendCell(row, "C5", "Programming");
        AppendCell(row, "D5", "3");
        AppendCell(row, "I5", "30");
        AppendCell(row, "J5", "LHP-1");
        AppendCell(row, "K5", "CL");
        AppendCell(row, "L5", "LT");
        AppendCell(row, "M5", "2");
        AppendCell(row, "N5", "1");
        if (includeRoom)
        {
            AppendCell(row, "O5", "101-A");
        }
        AppendCell(row, "P5", "Alice");
        var sheetData = new SheetData(row);
        if (includeValidSession)
        {
            var validRow = new Row { RowIndex = 6 };
            AppendCell(validRow, "A6", "K69I-IT1");
            AppendCell(validRow, "B6", "INT1000");
            AppendCell(validRow, "C6", "Programming");
            AppendCell(validRow, "D6", "3");
            AppendCell(validRow, "I6", "30");
            AppendCell(validRow, "J6", "LHP-2");
            AppendCell(validRow, "K6", "CL");
            AppendCell(validRow, "L6", "LT");
            AppendCell(validRow, "M6", "2");
            AppendCell(validRow, "N6", "2");
            AppendCell(validRow, "O6", "101-B");
            AppendCell(validRow, "P6", "Alice");
            sheetData.Append(validRow);
        }
        if (includeUnresolvedSession)
        {
            var unresolvedRow = new Row { RowIndex = 6 };
            AppendCell(unresolvedRow, "A6", "K69I-IT1");
            AppendCell(unresolvedRow, "B6", "INT1000");
            AppendCell(unresolvedRow, "C6", "Programming");
            AppendCell(unresolvedRow, "D6", "3");
            AppendCell(unresolvedRow, "I6", "30");
            AppendCell(unresolvedRow, "J6", "LHP-2");
            AppendCell(unresolvedRow, "K6", "CL");
            AppendCell(unresolvedRow, "L6", "TH");
            AppendCell(unresolvedRow, "M6", "Thông báo sau");
            AppendCell(unresolvedRow, "P6", "Bob");
            sheetData.Append(unresolvedRow);
        }

        worksheetPart.Worksheet = new Worksheet(sheetData);

        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Sheet3",
        });
        workbookPart.Workbook.Save();
        return path;
    }

    private static string? FindSignedPdf()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = directory.GetFiles("*.pdf")
                .FirstOrDefault(file => !file.Name.Equals("constraint.pdf", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null)
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    private static void AppendCell(Row row, string reference, string value)
    {
        row.Append(new Cell
        {
            CellReference = reference,
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value)),
        });
    }

    private sealed class SatisfyingSolver : IPersonalSelectionSatSolver
    {
        public Task<PersonalSelectionSatResult> SolveAsync(
            PersonalSelectionCnf cnf,
            int maxSolutions,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Feasible,
            ImmutableArray.Create(Enumerable.Range(1, cnf.VariableCount).ToImmutableArray()),
            TimeSpan.FromMilliseconds(12),
                1));
    }

    private sealed class InfeasibleSolver : IPersonalSelectionSatSolver
    {
        public Task<PersonalSelectionSatResult> SolveAsync(
            PersonalSelectionCnf cnf,
            int maxSolutions,
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult(new PersonalSelectionSatResult(
            PersonalSelectionSatStatus.Infeasible,
            [],
            TimeSpan.FromMilliseconds(12),
            1));
    }

    private sealed class BlockingSolver : IPersonalSelectionSatSolver
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PersonalSelectionSatResult> SolveAsync(
            PersonalSelectionCnf cnf,
            int maxSolutions,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not stop the solver.");
        }
    }
}
