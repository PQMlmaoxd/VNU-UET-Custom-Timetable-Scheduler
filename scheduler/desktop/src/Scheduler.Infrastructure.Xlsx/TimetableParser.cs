using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Scheduler.Domain;
using Scheduler.Infrastructure.Timetable;

namespace Scheduler.Infrastructure.Xlsx;

public sealed class TimetableParser
{
    public const string SheetName = "Sheet3";
    public const uint DataStartRow = 5;
    public const int MaximumRows = 200_000;
    public const int MaximumSharedStrings = 1_000_000;
    public const long MaximumFileBytes = 25L * 1024 * 1024;
    public const int MaximumArchiveEntries = 10_000;
    public const long MaximumArchiveEntryBytes = 64L * 1024 * 1024;
    public const long MaximumArchiveUncompressedBytes = 128L * 1024 * 1024;

    private const uint ClassColumn = 1;
    private const uint CourseCodeColumn = 2;
    private const uint CourseNameColumn = 3;
    private const uint CreditsColumn = 4;
    private const uint LtHoursColumn = 5;
    private const uint ThHoursColumn = 6;
    private const uint ClassSizeColumn = 9;
    private const uint LhpColumn = 10;
    private const uint GroupColumn = 11;
    private const uint SessionTypeColumn = 12;
    private const uint DayColumn = 13;
    private const uint PeriodColumn = 14;
    private const uint RoomColumn = 15;
    private const uint LecturerColumn = 16;
    private const uint Note1Column = 17;
    private const uint Note2Column = 18;

    private static readonly TimetableColumns LegacyColumns = new(
        ClassColumn,
        CourseCodeColumn,
        CourseNameColumn,
        CreditsColumn,
        LtHoursColumn,
        ThHoursColumn,
        ClassSizeColumn,
        LhpColumn,
        GroupColumn,
        SessionTypeColumn,
        DayColumn,
        PeriodColumn,
        RoomColumn,
        LecturerColumn,
        Note1Column,
        Note2Column);

    public static TimetableParseResult Parse(
        string workbookPath,
        string? departmentFilter = "ALL",
        string problemId = "HKII-2025-2026",
        string semester = "HKII 2025-2026",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateArchive(workbookPath, cancellationToken);

        var courses = new Dictionary<string, Course>(StringComparer.Ordinal);
        var rooms = new Dictionary<string, Room>(StringComparer.Ordinal);
        var lecturers = new Dictionary<string, Lecturer>(StringComparer.Ordinal);
        var inScope = new List<Session>();
        var otherDepartment = new List<Session>();
        var warnings = new List<string>();
        var fatalWarnings = new List<string>();
        var skippedRows = new List<uint>();
        var quarantinedLocations = new Dictionary<(string CourseCode, string LhpCode), HashSet<string>>();
        var quarantinedRowCounts = new Dictionary<(string CourseCode, string LhpCode), int>();

        using var document = SpreadsheetDocument.Open(workbookPath, false);
        var workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("Workbook has no workbook part.");
        var sharedStrings = ReadSharedStrings(workbookPart, cancellationToken);
        var profiles = ResolveSheetProfiles(workbookPart, sharedStrings, cancellationToken);
        if (profiles.IsDefaultOrEmpty)
        {
            var sheetNames = workbookPart.Workbook.Sheets?.Elements<Sheet>()
                .Select(candidate => candidate.Name?.Value ?? string.Empty)
                .ToArray() ?? [];
            throw new InvalidDataException(
                $"Expected sheet '{SheetName}' or a sheet with timetable headers, found [{string.Join(", ", sheetNames)}].");
        }

        uint totalRowsProcessed = 0;
        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(profile.Sheet.Id!);
            var rows = worksheetPart.Worksheet.GetFirstChild<SheetData>()?
                .Elements<Row>()
                .Take(MaximumRows + 1)
                .ToArray() ?? [];
            if (rows.Length > MaximumRows)
            {
                throw new InvalidDataException($"Sheet '{profile.Name}' contains more than {MaximumRows} rows.");
            }

            var dataRows = rows.Where(row => (row.RowIndex?.Value ?? 0) >= profile.DataStartRow).ToArray();
            if (profile.IsLegacy && profiles.Length == 1)
            {
                var maxRow = rows.Select(row => row.RowIndex?.Value ?? 0U).DefaultIfEmpty(0U).Max();
                totalRowsProcessed = maxRow >= profile.DataStartRow ? maxRow - profile.DataStartRow + 1 : 0;
            }
            else
            {
                totalRowsProcessed = checked(totalRowsProcessed + (uint)dataRows.Length);
            }

            var parsedRowCount = 0;
            foreach (var row in dataRows)
            {
                if ((parsedRowCount++ & 0x3ff) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var rowIndex = row.RowIndex?.Value ?? 0;
                var cells = ReadCells(row, sharedStrings);
                var classCode = ReadCell(cells, profile.Columns.Class);
                var courseCode = ReadCell(cells, profile.Columns.CourseCode);

                if (classCode is null || courseCode is null || TimetableSemantics.IsPhysicalEducationSection(classCode))
                {
                    continue;
                }

                // Header-driven templates interleave curriculum headings with session rows.
                // A row without an LHP identifier cannot represent a selectable timetable session.
                if (!profile.IsLegacy && ReadCell(cells, profile.Columns.Lhp) is null)
                {
                    continue;
                }

                var parsed = ParseRow(cells, rowIndex, profile, classCode, courses, rooms, lecturers);
                if (parsed.Warning is not null)
                {
                    var warningPrefix = profile.IsLegacy ? $"Row {rowIndex}" : $"{profile.Name} row {rowIndex}";
                    var warning = $"{warningPrefix}: {parsed.Warning}";
                    warnings.Add(warning);
                    if (parsed.IsFatal)
                    {
                        fatalWarnings.Add(warning);
                    }
                    if (parsed.Quarantine is { } quarantine)
                    {
                        var key = (quarantine.CourseCode, quarantine.LhpCode);
                        if (!quarantinedLocations.TryGetValue(key, out var locations))
                        {
                            locations = new HashSet<string>(StringComparer.Ordinal);
                            quarantinedLocations.Add(key, locations);
                        }

                        locations.Add(quarantine.SourceLocation);
                        quarantinedRowCounts[key] = quarantinedRowCounts.GetValueOrDefault(key) + 1;
                    }
                    skippedRows.Add(rowIndex);
                    continue;
                }

                var session = parsed.Session!;
                if (string.Equals(departmentFilter ?? "ALL", "CNTT", StringComparison.OrdinalIgnoreCase) &&
                    !TimetableSemantics.IsCnttClass(classCode))
                {
                    otherDepartment.Add(session);
                }
                else
                {
                    inScope.Add(session);
                }
            }
        }

        var quarantinedKeys = quarantinedLocations.Keys.ToHashSet();
        var filteredInScope = inScope
            .Where(session => !quarantinedKeys.Contains((session.Course.Code, session.LhpCode)))
            .ToList();
        var filteredOtherDepartment = otherDepartment
            .Where(session => !quarantinedKeys.Contains((session.Course.Code, session.LhpCode)))
            .ToList();

        if (filteredInScope.Count == 0 && filteredOtherDepartment.Count == 0)
        {
            if (quarantinedLocations.Count > 0)
            {
                throw new InvalidDataException(
                    "The workbook contains only timetable offerings whose physical schedules are unresolved.");
            }

            throw new InvalidDataException("No usable timetable sessions were found in the workbook.");
        }

        var retainedRoomCodes = filteredInScope
            .Concat(filteredOtherDepartment)
            .Select(session => session.Room?.Code)
            .Where(code => code is not null)
            .ToHashSet(StringComparer.Ordinal);
        var availableRooms = rooms.Values
            .Where(room => !room.IsVirtual && retainedRoomCodes.Contains(room.Code))
            .ToImmutableArray();
        var quarantinedOfferings = quarantinedLocations
            .OrderBy(pair => pair.Key.CourseCode, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.LhpCode, StringComparer.Ordinal)
            .Select(pair => new QuarantinedOffering(
                pair.Key.CourseCode,
                pair.Key.LhpCode,
                "unresolved_physical_schedule",
                pair.Value.Order(StringComparer.Ordinal).ToImmutableArray(),
                quarantinedRowCounts[pair.Key],
                checked(
                    inScope.Count(session => pair.Key == (session.Course.Code, session.LhpCode)) +
                    otherDepartment.Count(session => pair.Key == (session.Course.Code, session.LhpCode)))))
            .ToImmutableArray();

        var problem = new SchedulingProblem(
            problemId,
            departmentFilter ?? "ALL",
            semester,
            filteredInScope.ToImmutableArray(),
            TimetableSemantics.BuildAllTimeSlots(),
            availableRooms,
            TimetableSemantics.BuildLecturerBlocks(filteredOtherDepartment, filteredInScope));

        return new TimetableParseResult(
            problem,
            filteredOtherDepartment.ToImmutableArray(),
            warnings.ToImmutableArray(),
            skippedRows.ToImmutableArray(),
            totalRowsProcessed)
        {
            FatalWarnings = fatalWarnings.ToImmutableArray(),
            QuarantinedOfferings = quarantinedOfferings,
        };
    }

    private static ImmutableArray<TimetableSheetProfile> ResolveSheetProfiles(
        WorkbookPart workbookPart,
        IReadOnlyList<string> sharedStrings,
        CancellationToken cancellationToken)
    {
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>().ToArray() ?? [];
        var legacySheet = sheets.SingleOrDefault(sheet =>
            sheet.Name?.Value == SheetName && IsVisibleSheet(sheet));
        if (legacySheet is not null)
        {
            var legacyPart = (WorksheetPart)workbookPart.GetPartById(legacySheet.Id!);
            if (HasLegacySessionRows(legacyPart, sharedStrings, cancellationToken))
            {
                return ImmutableArray.Create(new TimetableSheetProfile(
                    legacySheet,
                    LegacyColumns,
                    DataStartRow,
                    IsLegacy: true,
                    SheetOrdinal: 0));
            }
        }

        var profiles = ImmutableArray.CreateBuilder<TimetableSheetProfile>();
        for (var index = 0; index < sheets.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = sheets[index];
            if (!IsVisibleSheet(sheet))
            {
                continue;
            }

            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id!);
            if (TryCreateHeaderProfile(sheet, worksheetPart, sharedStrings, index + 1, out var profile))
            {
                profiles.Add(profile);
            }
        }

        return profiles.ToImmutable();
    }

    private static bool IsVisibleSheet(Sheet sheet)
    {
        var state = sheet.State?.Value;
        return state is null || state == SheetStateValues.Visible;
    }

    private static bool HasLegacySessionRows(
        WorksheetPart worksheetPart,
        IReadOnlyList<string> sharedStrings,
        CancellationToken cancellationToken)
    {
        var rows = worksheetPart.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>() ?? [];
        var inspected = 0;
        foreach (var row in rows)
        {
            if ((inspected++ & 0x3ff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if ((row.RowIndex?.Value ?? 0) < DataStartRow)
            {
                continue;
            }

            var cells = ReadCells(row, sharedStrings);
            var classCode = ReadCell(cells, ClassColumn);
            var courseCode = ReadCell(cells, CourseCodeColumn);
            var lhpCode = ReadCell(cells, LhpColumn);
            if (!string.IsNullOrWhiteSpace(classCode) &&
                !string.IsNullOrWhiteSpace(courseCode) &&
                !string.IsNullOrWhiteSpace(lhpCode) &&
                !TimetableSemantics.IsPhysicalEducationSection(classCode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateHeaderProfile(
        Sheet sheet,
        WorksheetPart worksheetPart,
        IReadOnlyList<string> sharedStrings,
        int sheetOrdinal,
        out TimetableSheetProfile profile)
    {
        var rows = worksheetPart.Worksheet.GetFirstChild<SheetData>()?.Elements<Row>()
            .Take(25)
            .ToArray() ?? [];
        foreach (var row in rows)
        {
            var columns = TryResolveColumns(ReadCells(row, sharedStrings));
            if (columns is not null)
            {
                profile = new TimetableSheetProfile(
                    sheet,
                    columns,
                    checked((row.RowIndex?.Value ?? 0) + 1),
                    IsLegacy: false,
                    SheetOrdinal: sheetOrdinal);
                return true;
            }
        }

        profile = null!;
        return false;
    }

    private static TimetableColumns? TryResolveColumns(IReadOnlyDictionary<uint, string?> cells)
    {
        var headers = cells
            .Where(pair => pair.Value is not null)
            .GroupBy(pair => NormalizeHeader(pair.Value!), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);

        uint? Find(params string[] aliases) => aliases
            .Select(alias => headers.TryGetValue(alias, out var column) ? (uint?)column : null)
            .FirstOrDefault(column => column is not null);

        var columns = new TimetableColumns(
            Find("lop"),
            Find("mahp", "mahocphan"),
            Find("mon", "tenmon", "tenhocphan"),
            Find("tc", "sotinchi"),
            Find("lt"),
            Find("th", "btth"),
            Find("sslop", "siso"),
            Find("malhp", "lophocphan"),
            Find("nhom"),
            Find("ltbtth", "ltth", "loai"),
            Find("thu"),
            Find("ca"),
            Find("gd", "giangduong", "phong", "phonghoc"),
            Find("giangvien", "gv"),
            Find("ghichuhoc", "ghichu"),
            null);

        return columns.HasRequiredScheduleColumns ? columns : null;
    }

    private static string NormalizeHeader(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(character == 'đ' ? 'd' : character);
        }

        return new string(builder
            .ToString()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static void ValidateArchive(string workbookPath, CancellationToken cancellationToken)
    {
        var file = new FileInfo(workbookPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Workbook file was not found.", workbookPath);
        }
        if (file.Length <= 0 || file.Length > MaximumFileBytes)
        {
            throw new InvalidDataException($"Workbook file size exceeds the {MaximumFileBytes / (1024 * 1024)} MB limit.");
        }

        try
        {
            using var stream = File.OpenRead(workbookPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumArchiveEntries)
            {
                throw new InvalidDataException($"Workbook archive contains more than {MaximumArchiveEntries} entries.");
            }

            long uncompressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Length > MaximumArchiveEntryBytes)
                {
                    throw new InvalidDataException("Workbook archive contains an entry that exceeds the supported size.");
                }

                uncompressedBytes = checked(uncompressedBytes + entry.Length);
                if (uncompressedBytes > MaximumArchiveUncompressedBytes)
                {
                    throw new InvalidDataException("Workbook archive expands beyond the supported size.");
                }
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            throw new InvalidDataException("Workbook is not a valid XLSX archive.", exception);
        }
    }

    private static ParsedRow ParseRow(
        IReadOnlyDictionary<uint, string?> cells,
        uint rowIndex,
        TimetableSheetProfile profile,
        string classCode,
        Dictionary<string, Course> courses,
        Dictionary<string, Room> rooms,
        Dictionary<string, Lecturer> lecturers)
    {
        var columns = profile.Columns;
        var rawCourseCode = ReadCell(cells, columns.CourseCode);
        var rawLhpCode = ReadCell(cells, columns.Lhp);
        var courseCode = rawCourseCode ?? "UNKNOWN";
        var courseName = ReadCell(cells, columns.CourseName) ?? "Unknown Course";
        var lhpCode = rawLhpCode ?? $"{courseCode}_row_{rowIndex}";
        var group = ReadCell(cells, columns.Group) ?? "CL";
        var rawSessionType = ReadCell(cells, columns.SessionType);

        if (rawSessionType is null)
        {
            return ParsedRow.FromWarning("missing session type");
        }

        if (!TimetableSemantics.TryParseSessionType(rawSessionType, out var sessionType))
        {
            return ParsedRow.FromWarning($"unknown session type '{rawSessionType}'");
        }

        var rawLecturers = ReadCell(cells, columns.Lecturer);
        var day = ReadCell(cells, columns.Day);
        var period = ReadCell(cells, columns.Period);
        var rawRoom = ReadCell(cells, columns.Room);
        if (sessionType != SessionType.Onl &&
            rawCourseCode is not null &&
            rawLhpCode is not null &&
            (IsUnresolvedScheduleMarker(day) ||
             IsUnresolvedScheduleMarker(period) ||
             IsUnresolvedScheduleMarker(rawRoom)))
        {
            return ParsedRow.FromQuarantine(
                $"Session {profile.SessionId(rowIndex)}: physical schedule is unresolved " +
                $"(day='{day ?? ""}', period='{period ?? ""}', room='{rawRoom ?? ""}').",
                courseCode,
                lhpCode,
                $"{profile.Name} row {rowIndex}");
        }

        var sessionLecturers = rawLecturers is null
            ? ImmutableArray.Create(new Lecturer("TBA", LecturerType.Organization))
            : TimetableSemantics.Split(rawLecturers)
                .Select(name => GetOrCreateLecturer(name, lecturers))
                .ToImmutableArray();
        var cohorts = TimetableSemantics.Split(classCode).Select(code => new StudentCohort(code)).ToImmutableArray();
        var course = GetOrCreateCourse(
            courseCode,
            courseName,
            ReadInteger(cells, columns.Credits),
            ReadInteger(cells, columns.LtHours),
            ReadInteger(cells, columns.ThHours),
            courses);

        TimeSlot? timeSlot = null;
        if (day is not null && period is not null && TimetableSemantics.TryParseDay(day, out var parsedDay) &&
            TimetableSemantics.TryParsePeriod(period, out var parsedPeriod))
        {
            timeSlot = new TimeSlot(parsedDay, parsedPeriod);
        }

        Room? room = null;
        if (sessionType == SessionType.Onl)
        {
            room = GetOrCreateRoom("ONL", rooms);
        }
        else if (rawRoom is { } roomCode)
        {
            room = GetOrCreateRoom(roomCode, rooms);
        }

        var note = string.Join(
            " ",
            new[]
            {
                ReadCell(cells, columns.Note1),
                ReadCell(cells, columns.Note2),
            }.Where(value => value is not null));

        try
        {
            return ParsedRow.FromSession(new Session(
                profile.SessionId(rowIndex),
                lhpCode,
                course,
                sessionType,
                group,
                ReadInteger(cells, columns.ClassSize),
                sessionLecturers,
                cohorts,
                timeSlot,
                room,
                note,
                checked((int)rowIndex)));
        }
        catch (DomainValidationException exception)
        {
            return ParsedRow.FromWarning(
                exception.Message,
                IsFatalScheduleGap(sessionType, day, period, rawRoom));
        }
    }

    private static Course GetOrCreateCourse(
        string code,
        string name,
        int credits,
        int ltHours,
        int thHours,
        Dictionary<string, Course> courses)
    {
        if (!courses.TryGetValue(code, out var course))
        {
            course = new Course(code, name, credits, ltHours, thHours);
            courses.Add(code, course);
        }

        return course;
    }

    private static Room GetOrCreateRoom(string code, Dictionary<string, Room> rooms)
    {
        if (!rooms.TryGetValue(code, out var room))
        {
            room = new Room(code);
            rooms.Add(code, room);
        }

        return room;
    }

    private static Lecturer GetOrCreateLecturer(string name, Dictionary<string, Lecturer> lecturers)
    {
        if (!lecturers.TryGetValue(name, out var lecturer))
        {
            lecturer = new Lecturer(name);
            lecturers.Add(name, lecturer);
        }

        return lecturer;
    }

    private static int ReadInteger(IReadOnlyDictionary<uint, string?> cells, uint? column) =>
        double.TryParse(
            ReadCell(cells, column),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? checked((int)value)
            : 0;

    private static string? ReadCell(IReadOnlyDictionary<uint, string?> cells, uint? expectedColumn) =>
        expectedColumn is { } column && cells.TryGetValue(column, out var value) ? value : null;

    private static Dictionary<uint, string?> ReadCells(Row row, IReadOnlyList<string> sharedStrings)
    {
        var cells = new Dictionary<uint, string?>();
        foreach (var cell in row.Elements<Cell>())
        {
            var column = GetColumnIndex(cell.CellReference?.Value);
            if (column == 0)
            {
                continue;
            }

            cells[column] = ReadCellValue(cell, sharedStrings);
        }

        return cells;
    }

    private static string? ReadCellValue(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        var dataType = cell.DataType?.Value;
        var value = dataType == CellValues.SharedString
            ? ReadSharedString(sharedStrings, cell.CellValue?.Text)
            : dataType == CellValues.InlineString
                ? cell.InlineString?.InnerText
                : cell.CellValue?.Text;

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? ReadSharedString(IReadOnlyList<string> sharedStrings, string? indexText)
    {
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
            index < 0 ||
            index >= sharedStrings.Count)
        {
            return null;
        }

        return sharedStrings[index];
    }

    private static List<string> ReadSharedStrings(
        WorkbookPart workbookPart,
        CancellationToken cancellationToken)
    {
        var table = workbookPart.SharedStringTablePart?.SharedStringTable;
        if (table is null)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in table.Elements<SharedStringItem>())
        {
            if (values.Count >= MaximumSharedStrings)
            {
                throw new InvalidDataException($"Workbook contains more than {MaximumSharedStrings} shared strings.");
            }
            if ((values.Count & 0x3fff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            values.Add(item.InnerText);
        }

        return values;
    }

    private static uint GetColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference))
        {
            return 0;
        }

        uint column = 0;
        foreach (var character in cellReference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            column = checked(column * 26 + (uint)(char.ToUpperInvariant(character) - 'A' + 1));
        }

        return column;
    }

    private sealed record TimetableColumns(
        uint? Class,
        uint? CourseCode,
        uint? CourseName,
        uint? Credits,
        uint? LtHours,
        uint? ThHours,
        uint? ClassSize,
        uint? Lhp,
        uint? Group,
        uint? SessionType,
        uint? Day,
        uint? Period,
        uint? Room,
        uint? Lecturer,
        uint? Note1,
        uint? Note2)
    {
        public bool HasRequiredScheduleColumns =>
            Class is not null &&
            CourseCode is not null &&
            CourseName is not null &&
            Lhp is not null &&
            Group is not null &&
            SessionType is not null &&
            Day is not null &&
            Period is not null &&
            Room is not null &&
            Lecturer is not null;
    }

    private sealed record TimetableSheetProfile(
        Sheet Sheet,
        TimetableColumns Columns,
        uint DataStartRow,
        bool IsLegacy,
        int SheetOrdinal)
    {
        public string Name => Sheet.Name?.Value ?? $"Sheet{SheetOrdinal}";

        public string SessionId(uint rowIndex) => IsLegacy
            ? $"row_{rowIndex}"
            : $"sheet_{SheetOrdinal}_row_{rowIndex}";
    }

    private static bool IsFatalScheduleGap(
        SessionType sessionType,
        string? day,
        string? period,
        string? room)
    {
        if (sessionType == SessionType.Onl)
        {
            return false;
        }

        var hasTime = !string.IsNullOrWhiteSpace(day) || !string.IsNullOrWhiteSpace(period);
        var hasRoom = !string.IsNullOrWhiteSpace(room);
        return hasTime != hasRoom;
    }

    private static bool IsUnresolvedScheduleMarker(string? value) =>
        string.Equals(NormalizeHeader(value ?? string.Empty), "thongbaosau", StringComparison.Ordinal);

    private sealed record ParsedRow(
        Session? Session,
        string? Warning,
        bool IsFatal,
        QuarantineInfo? Quarantine)
    {
        public static ParsedRow FromSession(Session session) => new(session, null, false, null);

        public static ParsedRow FromWarning(string warning, bool isFatal = false) => new(null, warning, isFatal, null);

        public static ParsedRow FromQuarantine(
            string warning,
            string courseCode,
            string lhpCode,
            string sourceLocation) => new(
            null,
            warning,
            false,
            new QuarantineInfo(courseCode, lhpCode, sourceLocation));
    }

    private sealed record QuarantineInfo(string CourseCode, string LhpCode, string SourceLocation);
}
