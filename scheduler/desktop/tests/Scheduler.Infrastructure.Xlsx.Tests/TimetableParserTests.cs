using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Scheduler.Domain;
using Scheduler.Infrastructure.Timetable;
using Scheduler.Infrastructure.Xlsx;
using Xunit;
using Xunit.Sdk;

namespace Scheduler.Infrastructure.Xlsx.Tests;

public sealed class TimetableParserTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public TimetableParserTests() => Directory.CreateDirectory(_temporaryDirectory);

    [Fact]
    public void ParseCnttSeparatesOtherDepartmentsAndBuildsLecturerBlocks()
    {
        var workbookPath = CreateWorkbook(TimetableParser.SheetName, new[]
        {
            Row(5, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-1", "CL", "LT", "2", "1", "101-A", "Alice+Viện Khảo thí"),
            Row(6, "K69E-EC1", "ELT1000", "Signals", "2", "20", "LHP-2", "CL", "TH", "2", "2", "201-B", "Alice"),
            Row(7, "K69I-CS1", "INT2000", "Online", "2", "20", "LHP-3", "CL", "ONL", null, null, null, null),
            Row(8, "Tất cả", "PE1000", "Physical education", "1", "20", "LHP-4", "CL", "LT", "2", "1", "101-A", "Coach"),
            Row(9, "K69I-IT2", "INT3000", "Malformed", "3", "30", "LHP-5", "CL", "UNKNOWN", "2", "1", "101-A", "Bob"),
        });

        var result = TimetableParser.Parse(workbookPath, "CNTT");

        Assert.Equal((uint)5, result.TotalRowsProcessed);
        Assert.Equal(2, result.Problem.Sessions.Length);
        Assert.Single(result.OtherDepartmentSessions);
        Assert.Equal(new uint[] { 9 }, result.SkippedRows);
        Assert.Equal("Row 9: unknown session type 'UNKNOWN'", Assert.Single(result.Warnings));
        Assert.Equal(24, result.Problem.AvailableTimeSlots.Length);
        Assert.Equal("101-A|201-B", string.Join('|', result.Problem.AvailableRooms.Select(room => room.Code)));

        var lecture = Assert.Single(result.Problem.Sessions.Where(session => session.SessionType == SessionType.Lt));
        Assert.Equal("LHP-1", lecture.LhpCode);
        Assert.Equal("Alice|Viện Khảo thí", string.Join('|', lecture.Lecturers.Select(lecturer => lecturer.Name)));
        Assert.Equal("Alice", string.Join('|', lecture.IndividualLecturers.Select(lecturer => lecturer.Name)));

        var online = Assert.Single(result.Problem.OnlineSessions);
        Assert.Null(online.TimeSlot);
        Assert.Equal("ONL", online.Room?.Code);

        var block = Assert.Single(result.Problem.LecturerBlocks);
        Assert.Equal("Alice", block.Lecturer.Name);
        Assert.Equal(new TimeSlot(Day.Monday, Period.Ca2), block.BlockedTimeSlot);
        Assert.Equal("Teaching LHP-2 outside target department", block.Reason);
    }

    [Fact]
    public void ParseMarksPhysicalScheduleGapAsFatalWarning()
    {
        var workbookPath = CreateWorkbook(TimetableParser.SheetName, new[]
        {
            Row(5, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-1", "CL", "LT", "2", "1", null, "Alice"),
            Row(6, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-2", "CL", "LT", "2", "2", "101-A", "Alice"),
        });

        var result = TimetableParser.Parse(workbookPath);

        Assert.Equal(new uint[] { 5 }, result.SkippedRows);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("Row 5", warning, StringComparison.Ordinal);
        Assert.Contains("non-ONL session must have both timeslot and room", warning, StringComparison.Ordinal);
        var fatalWarning = Assert.Single(result.FatalWarnings);
        Assert.Equal(warning, fatalWarning);
    }

    [Theory]
    [InlineData("Thông báo sau", "1", "101-A")]
    [InlineData("2", "Thông báo sau", "101-A")]
    [InlineData("2", "1", "Thông báo sau")]
    public void ParseQuarantinesUnresolvedPhysicalScheduleMarkers(string day, string period, string room)
    {
        var workbookPath = CreateWorkbook(TimetableParser.SheetName, new[]
        {
            Row(5, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-1", "CL", "TH", day, period, room, "Alice"),
            Row(6, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-2", "CL", "LT", "2", "2", "102-A", "Bob"),
        });

        var result = TimetableParser.Parse(workbookPath);

        Assert.Empty(result.FatalWarnings);
        Assert.Equal(new uint[] { 5 }, result.SkippedRows);
        var offering = Assert.Single(result.QuarantinedOfferings);
        Assert.Equal("INT1000", offering.CourseCode);
        Assert.Equal("LHP-1", offering.LhpCode);
        Assert.Equal(1, offering.QuarantinedRowCount);
        Assert.Equal(0, offering.ExcludedSessionCount);
        Assert.Contains("Sheet3 row 5", offering.SourceLocations);
        Assert.DoesNotContain(result.Problem.Sessions, session => session.LhpCode == "LHP-1");
        Assert.Contains(result.Problem.Sessions, session => session.LhpCode == "LHP-2");
    }

    [Fact]
    public void ParseQuarantinesAnEntireOfferingWhenOneComponentIsUnresolved()
    {
        var workbookPath = CreateWorkbook(TimetableParser.SheetName, new[]
        {
            Row(5, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-1", "CL", "LT", "2", "1", "101-A", "Alice"),
            Row(6, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-1", "CL", "TH", "Thông báo sau", null, null, "Bob"),
            Row(7, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-2", "CL", "LT", "2", "2", "102-A", "Alice"),
        });

        var result = TimetableParser.Parse(workbookPath);

        var offering = Assert.Single(result.QuarantinedOfferings);
        Assert.Equal(1, offering.QuarantinedRowCount);
        Assert.Equal(1, offering.ExcludedSessionCount);
        Assert.DoesNotContain(result.Problem.Sessions, session => session.LhpCode == "LHP-1");
        Assert.Contains(result.Problem.Sessions, session => session.LhpCode == "LHP-2");
        Assert.DoesNotContain(result.Problem.AvailableRooms, room => room.Code == "101-A");
        Assert.Contains(result.Problem.AvailableRooms, room => room.Code == "102-A");
    }

    [Fact]
    public void ParseDoesNotQuarantineAnUnresolvedMarkerInNotes()
    {
        var workbookPath = CreateWorkbook(TimetableParser.SheetName, new[]
        {
            Row(5, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-1", "CL", "LT", "2", "1", "101-A", "Alice", "Thông báo sau"),
        });

        var result = TimetableParser.Parse(workbookPath);

        Assert.Single(result.Problem.Sessions);
        Assert.Empty(result.QuarantinedOfferings);
        Assert.Empty(result.FatalWarnings);
    }

    [Fact]
    public void ParseIgnoresHiddenAndVeryHiddenTimetableSheets()
    {
        var headers = new RowDefinition(1, new Dictionary<uint, string?>
        {
            [1] = "Lớp", [3] = "Mã HP", [4] = "Môn", [5] = "TC",
            [10] = "Mã LHP", [11] = "Nhóm", [13] = "LT/BT/TH",
            [14] = "Thứ", [15] = "Ca", [16] = "GĐ", [17] = "Giảng viên",
        });
        var visibleRow = HeaderRow(2, "K71I-IT1", "INT7000", "Networks", "3", "30", null, "80",
            "INT7000 1", "CL", "LT", "2", "1", "101-A", "Alice");
        var hiddenLegacyRow = Row(5, "K69I-IT1", "INT6000", "Hidden legacy", "3", "30", "INT6000 1", "CL", "LT", "2", "1", "102-A", "Bob");
        var workbookPath = CreateWorkbook(
            new Dictionary<string, IReadOnlyCollection<RowDefinition>>
            {
                [TimetableParser.SheetName] = new[] { hiddenLegacyRow },
                ["Visible timetable"] = new[] { headers, visibleRow },
                ["Hidden timetable"] = new[] { headers, HeaderRow(2, "K71I-IT2", "INT7001", "Hidden", "3", "30", null, "80", "INT7001 1", "CL", "LT", "2", "1", "103-A", "Bob") },
                ["Very hidden timetable"] = new[] { headers, HeaderRow(2, "K71I-IT3", "INT7002", "Very hidden", "3", "30", null, "80", "INT7002 1", "CL", "LT", "2", "1", "104-A", "Carol") },
            },
            new Dictionary<string, SheetStateValues>
            {
                [TimetableParser.SheetName] = SheetStateValues.Hidden,
                ["Hidden timetable"] = SheetStateValues.Hidden,
                ["Very hidden timetable"] = SheetStateValues.VeryHidden,
            });

        var result = TimetableParser.Parse(workbookPath);

        var session = Assert.Single(result.Problem.Sessions);
        Assert.Equal("INT7000", session.Course.Code);
        Assert.Equal("sheet_2_row_2", session.SessionId);
    }

    [Fact]
    public void ParseRejectsWorkbookContainingOnlyUnresolvedOfferings()
    {
        var workbookPath = CreateWorkbook(TimetableParser.SheetName, new[]
        {
            Row(5, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-1", "CL", "TH", "Thông báo sau", null, null, "Alice"),
        });

        var exception = Assert.Throws<InvalidDataException>(() => TimetableParser.Parse(workbookPath));

        Assert.Contains("only timetable offerings", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAllKeepsAllValidSessionsWithoutCrossDepartmentBlocks()
    {
        var workbookPath = CreateWorkbook(TimetableParser.SheetName, new[]
        {
            Row(5, "K69I-IT1", "INT1000", "Programming", "3", "30", "LHP-1", "CL", "LT", "2", "1", "101-A", "Alice"),
            Row(6, "K69E-EC1", "ELT1000", "Signals", "2", "20", "LHP-2", "CL", "TH", "2", "2", "201-B", "Alice"),
        });

        var result = TimetableParser.Parse(workbookPath);

        Assert.Equal("ALL", result.Problem.Department);
        Assert.Equal(2, result.Problem.Sessions.Length);
        Assert.Empty(result.OtherDepartmentSessions);
        Assert.Empty(result.Problem.LecturerBlocks);
    }

    [Fact]
    public void ParseRejectsWorkbookWithoutExpectedSheet()
    {
        var workbookPath = CreateWorkbook("OtherSheet", []);

        var exception = Assert.Throws<InvalidDataException>(() => TimetableParser.Parse(workbookPath));

        Assert.Contains("Expected sheet 'Sheet3'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFallsBackToHeaderDrivenSheetsWhenSheet3IsEmpty()
    {
        var workbookPath = CreateWorkbook(new Dictionary<string, IReadOnlyCollection<RowDefinition>>
        {
            [TimetableParser.SheetName] = [],
            ["TKB K71"] = new[]
            {
                new RowDefinition(1, new Dictionary<uint, string?>
                {
                    [1] = "Lớp", [3] = "Mã HP", [4] = "Môn", [5] = "TC",
                    [10] = "Mã LHP", [11] = "Nhóm", [13] = "LT/BT/TH",
                    [14] = "Thứ", [15] = "Ca", [16] = "GĐ", [17] = "Giảng viên",
                }),
                HeaderRow(2, "K71I-IT1", "INT7000", "Networks", "3", "30", null, "80",
                    "INT7000 1", "CL", "LT", "2", "1", "101-A", "Alice"),
            },
        });

        var result = TimetableParser.Parse(workbookPath);

        var session = Assert.Single(result.Problem.Sessions);
        Assert.Equal("INT7000", session.Course.Code);
        Assert.Equal("sheet_2_row_2", session.SessionId);
    }

    [Fact]
    public void IsCnttClassAcceptsCnttWhenItIsNotTheFirstCohort()
    {
        Assert.True(TimetableSemantics.IsCnttClass("K71E-EC1+K71I-IT1"));
        Assert.True(TimetableSemantics.IsCnttClass("K71I-IT1+K71E-EC1"));
        Assert.False(TimetableSemantics.IsCnttClass("K71E-EC1+K71T-EE1"));
    }

    [Fact]
    public void ParseHonorsCancellationBeforeOpeningTheWorkbook()
    {
        var workbookPath = CreateWorkbook(TimetableParser.SheetName, []);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => TimetableParser.Parse(
            workbookPath,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ParseSemanticsAcceptsCommonHeaderDrivenScheduleTokens()
    {
        Assert.True(TimetableSemantics.TryParseSessionType("LT + BT", out var combined));
        Assert.Equal(SessionType.LtBt, combined);
        Assert.True(TimetableSemantics.TryParseSessionType("BT", out var exercise));
        Assert.Equal(SessionType.Bt, exercise);
        Assert.True(TimetableSemantics.TryParseDay("Thứ 4", out var day));
        Assert.Equal(Day.Wednesday, day);
        Assert.True(TimetableSemantics.TryParsePeriod("3 - 4", out var period));
        Assert.Equal(Period.Ca3To4, period);
        Assert.Equal("Alice|Bob", string.Join('|', TimetableSemantics.Split("Alice+Bob+Alice")));
    }

    [Fact]
    public void ParseDiscoversHeaderDrivenTimetableSheetsAndNormalizesBtSessions()
    {
        var headers = new RowDefinition(1, new Dictionary<uint, string?>
        {
            [1] = "Lớp",
            [3] = "Mã HP",
            [4] = "Môn",
            [5] = "TC",
            [6] = "LT",
            [7] = "BT/TH",
            [10] = "SS lớp",
            [11] = "Mã LHP",
            [12] = "Nhóm",
            [13] = "LT/BT/TH",
            [14] = "Thứ",
            [15] = "Ca",
            [16] = "Giảng đường",
            [17] = "Giảng viên",
            [18] = "Ghi chú học",
        });
        var workbookPath = CreateWorkbook(new Dictionary<string, IReadOnlyCollection<RowDefinition>>
        {
            ["TKB K70"] = new[]
            {
                headers,
                HeaderRow(2, "K70I-IT1", "UET.CS1000", "Algorithms", "3", "30", "15", "60", "UET.CS1000 1", "CL", "LT+BT", "2", "3", "107-B", "Alice"),
                HeaderRow(3, "K70I-IT1", "UET.CS1000", "Algorithms", "3", null, "15", "60", "UET.CS1000 1", "CL", "BT", "4", "2", "107-B", "Bob"),
            },
            ["TKB K71"] = new[]
            {
                headers,
                HeaderRow(2, "K71I-IT1", "VNU1001", "Digital literacy", "3", "45", null, "80", "VNU1001 1", "CL", "ONL", null, null, null, "Viện Khảo thí"),
            },
        });

        var result = TimetableParser.Parse(workbookPath);

        Assert.Equal(3, result.Problem.Sessions.Length);
        Assert.Equal((uint)3, result.TotalRowsProcessed);
        Assert.Contains(result.Problem.Sessions, session => session.SessionType == SessionType.LtBt);
        Assert.Contains(result.Problem.Sessions, session => session.SessionType == SessionType.Bt);
        Assert.Contains(result.Problem.OnlineSessions, session => session.Course.Code == "VNU1001");
        Assert.Equal("sheet_1_row_2|sheet_1_row_3|sheet_2_row_2", string.Join('|', result.Problem.Sessions.Select(session => session.SessionId)));
    }

    [Fact]
    public void ParseHeaderDrivenSheetAcceptsReorderedColumnsAndAliases()
    {
        var workbookPath = CreateWorkbook(new Dictionary<string, IReadOnlyCollection<RowDefinition>>
        {
            ["Kế hoạch học kỳ"] = new[]
            {
                new RowDefinition(3, new Dictionary<uint, string?>
                {
                    [1] = "Giảng viên",
                    [2] = "Mã học phần",
                    [3] = "Lớp học phần",
                    [4] = "Loại",
                    [5] = "Thứ",
                    [6] = "Ca",
                    [7] = "Phòng học",
                    [8] = "Lớp",
                    [9] = "Tên học phần",
                    [10] = "Sĩ số",
                    [11] = "Tín chỉ",
                    [12] = "Nhóm",
                    [13] = "LT",
                    [14] = "TH",
                    [15] = "Ghi chú",
                }),
                new RowDefinition(4, new Dictionary<uint, string?>
                {
                    [1] = "Alice",
                    [2] = "MAT1000",
                    [3] = "MAT1000 1",
                    [4] = "BT",
                    [5] = "Thứ 5",
                    [6] = "1 - 2",
                    [7] = "107-B",
                    [8] = "K71I-IT1",
                    [9] = "Mathematics",
                    [10] = "80",
                    [11] = "4",
                    [12] = "CL",
                    [13] = "30",
                    [14] = "15",
                    [15] = "Reordered template",
                }),
            },
        });

        var result = TimetableParser.Parse(workbookPath);

        var session = Assert.Single(result.Problem.Sessions);
        Assert.Equal("MAT1000", session.Course.Code);
        Assert.Equal("MAT1000 1", session.LhpCode);
        Assert.Equal(SessionType.Bt, session.SessionType);
        Assert.Equal(new TimeSlot(Day.Thursday, Period.Ca1To2), session.TimeSlot);
        Assert.Equal("107-B", session.Room?.Code);
        Assert.Equal("Reordered template", session.Note);
    }

    [Fact]
    [Trait("Category", "Compatibility")]
    public void ParseExternalWorkbookMatchesCompatibilityBaselineWhenAvailable()
    {
        var workbookPath = FindRealWorkbook();
        if (workbookPath is null)
        {
            throw SkipException.ForSkip("External XLSX fixture is not available.");
        }

        var all = TimetableParser.Parse(workbookPath, "ALL");
        var cntt = TimetableParser.Parse(workbookPath, "CNTT");

        AssertParseSummary(all, sessions: 1661, schedulable: 1611, online: 50, otherDepartment: 0, blocks: 0, rooms: 106, warnings: 2, rows: 1826);
        AssertParseSummary(cntt, sessions: 402, schedulable: 388, online: 14, otherDepartment: 1259, blocks: 218, rooms: 106, warnings: 2, rows: 1826);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, true);
    }

    private string CreateWorkbook(string sheetName, IReadOnlyCollection<RowDefinition> rows)
        => CreateWorkbook(new Dictionary<string, IReadOnlyCollection<RowDefinition>> { [sheetName] = rows });

    private string CreateWorkbook(
        IReadOnlyDictionary<string, IReadOnlyCollection<RowDefinition>> definitions,
        Dictionary<string, SheetStateValues>? sheetStates = null)
    {
        var path = Path.Combine(_temporaryDirectory, $"{Guid.NewGuid():N}.xlsx");
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new Sheets());
        uint sheetId = 1;
        foreach (var (sheetName, rows) in definitions)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            foreach (var definition in rows)
            {
                sheetData.Append(CreateRow(definition));
            }

            worksheetPart.Worksheet = new Worksheet(sheetData);
            var sheet = new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId++,
                Name = sheetName,
            };
            if (sheetStates?.TryGetValue(sheetName, out var state) == true)
            {
                sheet.State = state;
            }

            sheets.Append(sheet);
        }

        workbookPart.Workbook.Save();
        return path;
    }

    private static void AssertParseSummary(
        TimetableParseResult result,
        int sessions,
        int schedulable,
        int online,
        int otherDepartment,
        int blocks,
        int rooms,
        int warnings,
        uint rows)
    {
        Assert.Equal(sessions, result.Problem.Sessions.Length);
        Assert.Equal(schedulable, result.Problem.SchedulableSessions.Length);
        Assert.Equal(online, result.Problem.OnlineSessions.Length);
        Assert.Equal(otherDepartment, result.OtherDepartmentSessions.Length);
        Assert.Equal(blocks, result.Problem.LecturerBlocks.Length);
        Assert.Equal(rooms, result.Problem.AvailableRooms.Length);
        Assert.Equal(warnings, result.Warnings.Length);
        Assert.Equal(rows, result.TotalRowsProcessed);
    }

    private static string? FindRealWorkbook()
    {
        var configured = Environment.GetEnvironmentVariable("SCHEDULER_TEST_WORKBOOK");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = directory.GetFiles("*.xlsx").FirstOrDefault();
            if (candidate is not null)
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    private static Row CreateRow(RowDefinition definition)
    {
        var row = new Row { RowIndex = definition.Index };
        foreach (var (column, value) in definition.Cells)
        {
            if (value is not null)
            {
                row.Append(new Cell
                {
                    CellReference = $"{ColumnName(column)}{definition.Index}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(value)),
                });
            }
        }

        return row;
    }

    private static RowDefinition Row(
        uint index,
        string classCode,
        string courseCode,
        string courseName,
        string credits,
        string classSize,
        string lhpCode,
        string group,
        string sessionType,
        string? day,
        string? period,
        string? room,
        string? lecturer,
        string? note = null) => new(
        index,
        new Dictionary<uint, string?>
        {
            [1] = classCode,
            [2] = courseCode,
            [3] = courseName,
            [4] = credits,
            [9] = classSize,
            [10] = lhpCode,
            [11] = group,
            [12] = sessionType,
            [13] = day,
            [14] = period,
            [15] = room,
            [16] = lecturer,
            [17] = note,
        });

    private static RowDefinition HeaderRow(
        uint index,
        string classCode,
        string courseCode,
        string courseName,
        string credits,
        string? ltHours,
        string? thHours,
        string classSize,
        string lhpCode,
        string group,
        string sessionType,
        string? day,
        string? period,
        string? room,
        string? lecturer) => new(
        index,
        new Dictionary<uint, string?>
        {
            [1] = classCode,
            [3] = courseCode,
            [4] = courseName,
            [5] = credits,
            [6] = ltHours,
            [7] = thHours,
            [10] = classSize,
            [11] = lhpCode,
            [12] = group,
            [13] = sessionType,
            [14] = day,
            [15] = period,
            [16] = room,
            [17] = lecturer,
        });

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

    private sealed record RowDefinition(uint Index, IReadOnlyDictionary<uint, string?> Cells);
}
