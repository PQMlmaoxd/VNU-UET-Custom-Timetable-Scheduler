using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Scheduler.Domain;
using Scheduler.Infrastructure.Timetable;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Scheduler.Infrastructure.Pdf;

public static class PdfTimetableParser
{
    public const string TemplateId = "uet-timetable-landscape-v1";

    private const double ExpectedPageWidth = 792;
    private const double ExpectedPageHeight = 612;
    private const double DataStartTolerance = 0.5;

    // These bands are derived from the table header and are intentionally kept
    // in one place. The PDF is a printed table, so whitespace token positions
    // are not a stable parsing contract.
    private static readonly PdfColumnBands Columns = new(
        Class: (0, 74),
        CourseCode: (74, 118),
        CourseName: (118, 210),
        Credits: (210, 230),
        LtHours: (230, 250),
        ThHours: (250, 270),
        SelfStudy: (270, 292),
        TeachingLoad: (292, 312),
        ClassSize: (312, 335),
        Lhp: (320, 373),
        Group: (373, 400),
        SessionType: (400, 430),
        Day: (430, 459),
        Period: (459, 480),
        Room: (480, 523),
        // The first notes column starts around x=623. Keeping it out of the
        // lecturer band is important for rows with "Ca 1/Ca 2" annotations.
        Lecturer: (523, 623),
        Notes: (681, 792));

    public static TimetableParseResult Parse(
        string pdfPath,
        string? departmentFilter = "ALL",
        string problemId = "HKII-2025-2026",
        string semester = "HKII 2025-2026",
        PdfTimetableParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        var parseOptions = options ?? new PdfTimetableParseOptions();
        parseOptions.Validate();
        ValidateFile(pdfPath, parseOptions);

        var courses = new Dictionary<string, Course>(StringComparer.Ordinal);
        var rooms = new Dictionary<string, Room>(StringComparer.Ordinal);
        var lecturers = new Dictionary<string, Lecturer>(StringComparer.Ordinal);
        var inScope = new List<Session>();
        var otherDepartment = new List<Session>();
        var warnings = new List<string>();
        var fatalWarnings = new List<string>();
        var skippedRows = new List<uint>();
        var logicalRows = 0U;
        var tableHeaderSeen = false;

        using var document = PdfDocument.Open(pdfPath);
        if (document.NumberOfPages > parseOptions.MaxPages)
        {
            throw new InvalidDataException(
                $"PDF has {document.NumberOfPages.ToString(CultureInfo.InvariantCulture)} pages; " +
                $"the limit is {parseOptions.MaxPages.ToString(CultureInfo.InvariantCulture)}.");
        }

        var activeRows = new List<PdfLogicalRow>();
        for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(pageNumber);
            ValidatePage(page, pageNumber, parseOptions);
            var lines = ReadLines(page, pageNumber, parseOptions, cancellationToken);

            foreach (var line in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsTableHeader(line))
                {
                    tableHeaderSeen = true;
                    continue;
                }

                if (IsSectionBoundary(line))
                {
                    FlushRows(
                        activeRows,
                        ref logicalRows,
                        departmentFilter,
                        courses,
                        rooms,
                        lecturers,
                        inScope,
                        otherDepartment,
                        warnings,
                        fatalWarnings,
                        skippedRows,
                        cancellationToken);
                    activeRows.Clear();
                    continue;
                }

                if (TryStartRow(line, pageNumber, out var row))
                {
                    FlushRows(
                        activeRows,
                        ref logicalRows,
                        departmentFilter,
                        courses,
                        rooms,
                        lecturers,
                        inScope,
                        otherDepartment,
                        warnings,
                        fatalWarnings,
                        skippedRows,
                        cancellationToken);
                    activeRows.Clear();
                    activeRows.Add(row);
                    continue;
                }

                if (activeRows.Count > 0 && IsContinuation(line))
                {
                    activeRows[^1].Append(line);
                }
            }
        }

        FlushRows(
            activeRows,
            ref logicalRows,
            departmentFilter,
            courses,
            rooms,
            lecturers,
            inScope,
            otherDepartment,
            warnings,
            fatalWarnings,
            skippedRows,
            cancellationToken);

        if (!tableHeaderSeen || logicalRows == 0)
        {
            throw new InvalidDataException("PDF does not match the supported timetable template.");
        }

        var lecturerAliases = BuildLecturerAliases();
        if (lecturerAliases.Count > 0)
        {
            ReplaceSessions(inScope, lecturerAliases, lecturers);
            ReplaceSessions(otherDepartment, lecturerAliases, lecturers);
        }

        var problem = new SchedulingProblem(
            problemId,
            departmentFilter ?? "ALL",
            semester,
            inScope.ToImmutableArray(),
            TimetableSemantics.BuildAllTimeSlots(),
            rooms.Values.Where(room => !room.IsVirtual).ToImmutableArray(),
            TimetableSemantics.BuildLecturerBlocks(otherDepartment, inScope));

        return new TimetableParseResult(
            problem,
            otherDepartment.ToImmutableArray(),
            warnings.ToImmutableArray(),
            skippedRows.ToImmutableArray(),
            logicalRows)
        {
            FatalWarnings = fatalWarnings.ToImmutableArray(),
        };
    }

    private static Dictionary<string, string> BuildLecturerAliases() => new(StringComparer.Ordinal)
    {
        // The signed print profile clips the final surname for these lecturer
        // cells. The aliases are limited to this known template; do not infer
        // names from arbitrary prefixes in a future PDF profile.
        ["Trần Quang"] = "Trần Quang Duy",
        ["Trịnh Lê Hoàng"] = "Trịnh Lê Hoàng Long",
        ["Chu Thị Phương"] = "Chu Thị Phương Dung",
        ["Phạm Nguyễn Phú"] = "Phạm Nguyễn Phú Sĩ",
        ["Nguyễn Minh"] = "Nguyễn Minh Đoàn",
        ["Vũ Trọng"] = "Vũ Trọng Thanh",
        ["Nguyễn Tuấn"] = "Nguyễn Tuấn Hưng",
        ["Nguyễn Đăng"] = "Nguyễn Đăng Cơ",
    };

    private static void ReplaceSessions(
        List<Session> sessions,
        Dictionary<string, string> aliases,
        Dictionary<string, Lecturer> lecturers)
    {
        for (var index = 0; index < sessions.Count; index++)
        {
            var session = sessions[index];
            var normalizedLecturers = session.Lecturers
                .Select(lecturer => aliases.TryGetValue(lecturer.Name, out var alias)
                    ? GetOrCreateLecturer(alias, lecturers)
                    : lecturer)
                .ToImmutableArray();

            if (!normalizedLecturers.SequenceEqual(session.Lecturers))
            {
                sessions[index] = new Session(
                    session.SessionId,
                    session.LhpCode,
                    session.Course,
                    session.SessionType,
                    session.Group,
                    session.ClassSize,
                    normalizedLecturers,
                    session.StudentCohorts,
                    session.TimeSlot,
                    session.Room,
                    session.Note,
                    session.SourceRow);
            }
        }
    }

    private static void FlushRows(
        IReadOnlyCollection<PdfLogicalRow> rows,
        ref uint logicalRows,
        string? departmentFilter,
        Dictionary<string, Course> courses,
        Dictionary<string, Room> rooms,
        Dictionary<string, Lecturer> lecturers,
        List<Session> inScope,
        List<Session> otherDepartment,
        List<string> warnings,
        List<string> fatalWarnings,
        List<uint> skippedRows,
        CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            logicalRows = checked(logicalRows + 1);

            if (TimetableSemantics.IsPhysicalEducationSection(row.ClassCode))
            {
                continue;
            }

            var parsed = ParseRow(row, logicalRows, courses, rooms, lecturers);
            if (parsed.Warning is not null)
            {
                warnings.Add(parsed.Warning);
                if (parsed.IsFatal)
                {
                    fatalWarnings.Add(parsed.Warning);
                }
                skippedRows.Add(logicalRows);
                continue;
            }

            var session = parsed.Session!;
            if (string.Equals(departmentFilter ?? "ALL", "CNTT", StringComparison.OrdinalIgnoreCase) &&
                !TimetableSemantics.IsCnttClass(row.ClassCode))
            {
                otherDepartment.Add(session);
            }
            else
            {
                inScope.Add(session);
            }
        }
    }

    private static ParsedRow ParseRow(
        PdfLogicalRow row,
        uint sourceRow,
        Dictionary<string, Course> courses,
        Dictionary<string, Room> rooms,
        Dictionary<string, Lecturer> lecturers)
    {
        var courseCode = row.CourseCode;
        var courseName = row.CourseName;
        var lhpCode = row.LhpCode;
        var group = row.Group;
        var rawSessionType = row.SessionType;

        if (string.IsNullOrWhiteSpace(rawSessionType))
        {
            return ParsedRow.FromWarning(
                $"PDF row {sourceRow}: missing session type " +
                $"(class='{row.ClassCode}', code='{row.CourseCode}', lhp='{row.LhpCode}', " +
                $"group='{row.Group}', day='{row.Day}', period='{row.Period}', room='{row.Room}')");
        }

        if (!TimetableSemantics.TryParseSessionType(rawSessionType, out var sessionType))
        {
            return ParsedRow.FromWarning(
                $"PDF row {sourceRow}: unknown session type '{rawSessionType}'");
        }

        if (string.IsNullOrWhiteSpace(courseCode) || string.IsNullOrWhiteSpace(courseName) ||
            string.IsNullOrWhiteSpace(lhpCode) || string.IsNullOrWhiteSpace(group))
        {
            return ParsedRow.FromWarning($"PDF row {sourceRow}: missing course or LHP fields");
        }

        var sessionLecturers = string.IsNullOrWhiteSpace(row.Lecturer)
            ? ImmutableArray.Create(new Lecturer("TBA", LecturerType.Organization))
            : TimetableSemantics.Split(row.Lecturer)
                .Select(name => GetOrCreateLecturer(name, lecturers))
                .ToImmutableArray();
        var cohorts = TimetableSemantics.Split(row.ClassCode)
            .Select(code => new StudentCohort(code))
            .ToImmutableArray();
        var course = GetOrCreateCourse(
            courseCode,
            courseName,
            ParseInteger(row.Credits),
            ParseInteger(row.LtHours),
            ParseInteger(row.ThHours),
            courses);

        TimeSlot? timeSlot = null;
        if (TimetableSemantics.TryParseDay(row.Day, out var day) &&
            TimetableSemantics.TryParsePeriod(row.Period, out var period))
        {
            timeSlot = new TimeSlot(day, period);
        }

        Room? room = null;
        if (sessionType == SessionType.Onl)
        {
            room = GetOrCreateRoom("ONL", rooms);
        }
        else if (!string.IsNullOrWhiteSpace(row.Room))
        {
            room = GetOrCreateRoom(NormalizeRoomCode(row.Room), rooms);
        }

        try
        {
            return ParsedRow.FromSession(new Session(
                $"pdf_row_{sourceRow.ToString(CultureInfo.InvariantCulture)}",
                lhpCode,
                course,
                sessionType,
                group,
                ParseInteger(row.ClassSize),
                sessionLecturers,
                cohorts,
                timeSlot,
                room,
                row.Notes,
                checked((int)sourceRow)));
        }
        catch (DomainValidationException exception)
        {
            return ParsedRow.FromWarning(
                $"PDF row {sourceRow}: {exception.Message} " +
                $"(type='{rawSessionType}', day='{row.Day}', period='{row.Period}', room='{row.Room}')",
                IsFatalScheduleGap(sessionType, row.Day, row.Period, row.Room));
        }
    }

    private static PdfVisualLine[] ReadLines(
        Page page,
        int pageNumber,
        PdfTimetableParseOptions options,
        CancellationToken cancellationToken)
    {
        var words = page.GetWords().Take(options.MaxWordsPerPage + 1).ToArray();
        if (words.Length > options.MaxWordsPerPage)
        {
            throw new InvalidDataException(
                $"PDF page {pageNumber.ToString(CultureInfo.InvariantCulture)} contains too many words.");
        }

        var lines = new List<PdfVisualLine>();
        PdfVisualLine? currentLine = null;
        var wordIndex = 0;
        foreach (var word in words.OrderByDescending(word => word.BoundingBox.Bottom)
                     .ThenBy(word => word.BoundingBox.Left))
        {
            if ((wordIndex++ & 0x3ff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (currentLine is null || Math.Abs(currentLine.Bottom - word.BoundingBox.Bottom) > options.LineTolerance)
            {
                currentLine = new PdfVisualLine(pageNumber, word.BoundingBox.Bottom);
                lines.Add(currentLine);
            }

            currentLine.Add(word, Columns);
        }

        return lines.OrderByDescending(line => line.Bottom).ToArray();
    }

    private static bool TryStartRow(
        PdfVisualLine line,
        int pageNumber,
        out PdfLogicalRow row)
    {
        row = null!;
        if (string.IsNullOrWhiteSpace(line.ClassCode) ||
            !LooksLikeClassCode(line.ClassCode) ||
            string.IsNullOrWhiteSpace(line.CourseCode) ||
            !LooksLikeCourseCode(line.CourseCode))
        {
            return false;
        }

        row = new PdfLogicalRow(pageNumber, line);
        return true;
    }

    private static bool IsContinuation(PdfVisualLine line) =>
        !string.IsNullOrWhiteSpace(line.ClassCode) ||
        !string.IsNullOrWhiteSpace(line.CourseName) ||
        !string.IsNullOrWhiteSpace(line.Lecturer) ||
        !string.IsNullOrWhiteSpace(line.Notes);

    private static bool IsTableHeader(PdfVisualLine line) =>
        line.ClassCode.Equals("Lớp", StringComparison.OrdinalIgnoreCase) &&
        line.CourseCode.Contains("Mã", StringComparison.OrdinalIgnoreCase) &&
        line.CourseName.Equals("Môn", StringComparison.OrdinalIgnoreCase);

    private static bool IsSectionBoundary(PdfVisualLine line) =>
        line.CourseName.Contains("Tổng TC", StringComparison.OrdinalIgnoreCase) ||
        line.ClassCode.Contains("Giáo dục thể chất", StringComparison.OrdinalIgnoreCase) ||
        line.CourseName.Contains("Giáo dục thể chất", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCourseCode(string value) =>
        value.Length >= 3 && value.Any(char.IsLetterOrDigit);

    private static bool LooksLikeClassCode(string value) =>
        (value.Length > 1 && value.StartsWith('K') && char.IsDigit(value[1])) ||
        value.Equals("Tất cả", StringComparison.Ordinal);

    private static void ValidateFile(string path, PdfTimetableParseOptions options)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("PDF timetable file was not found.", path);
        }

        if (fileInfo.Length <= 0 || fileInfo.Length > options.MaxFileBytes)
        {
            throw new InvalidDataException("PDF timetable file size is outside the supported limit.");
        }

        Span<byte> signature = stackalloc byte[5];
        using var stream = File.OpenRead(path);
        if (stream.Read(signature) != signature.Length ||
            !signature.SequenceEqual("%PDF-"u8))
        {
            throw new InvalidDataException("The file is not a valid PDF document.");
        }
    }

    private static void ValidatePage(
        Page page,
        int pageNumber,
        PdfTimetableParseOptions options)
    {
        if (Math.Abs(page.Width - ExpectedPageWidth) > options.PageWidthTolerance ||
            Math.Abs(page.Height - ExpectedPageHeight) > options.PageHeightTolerance)
        {
            throw new InvalidDataException(
                $"PDF page {pageNumber.ToString(CultureInfo.InvariantCulture)} has an unsupported size.");
        }
    }

    private static int ParseInteger(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;

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

    private static string NormalizeRoomCode(string value) => value switch
    {
        // The signed print template omits the campus suffix used by the XLSX
        // source for these two rooms. Keep their physical movement zone.
        "803-T5" => "803-T5 ĐHKHTN",
        "807-T5" => "807-T5 ĐHKHTN",
        _ => value,
    };

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

    private sealed record ParsedRow(Session? Session, string? Warning, bool IsFatal)
    {
        public static ParsedRow FromSession(Session session) => new(session, null, false);

        public static ParsedRow FromWarning(string warning, bool isFatal = false) => new(null, warning, isFatal);
    }

    private sealed class PdfLogicalRow
    {
        public PdfLogicalRow(int pageNumber, PdfVisualLine firstLine)
        {
            PageNumber = pageNumber;
            ClassCode = firstLine.ClassCode;
            CourseCode = firstLine.CourseCode;
            CourseName = firstLine.CourseName;
            Credits = firstLine.Credits;
            LtHours = firstLine.LtHours;
            ThHours = firstLine.ThHours;
            ClassSize = firstLine.ClassSize;
            LhpCode = firstLine.LhpCode;
            Group = firstLine.Group;
            SessionType = firstLine.SessionType;
            Day = firstLine.Day;
            Period = firstLine.Period;
            Room = firstLine.Room;
            Lecturer = firstLine.Lecturer;
            Notes = firstLine.Notes;
        }

        public int PageNumber { get; }

        public string ClassCode { get; private set; }

        public string CourseCode { get; private set; }

        public string CourseName { get; private set; }

        public string Credits { get; private set; }

        public string LtHours { get; private set; }

        public string ThHours { get; private set; }

        public string ClassSize { get; private set; }

        public string LhpCode { get; private set; }

        public string Group { get; private set; }

        public string SessionType { get; private set; }

        public string Day { get; private set; }

        public string Period { get; private set; }

        public string Room { get; private set; }

        public string Lecturer { get; private set; }

        public string Notes { get; private set; }

        public void Append(PdfVisualLine line)
        {
            ClassCode = AppendClass(ClassCode, line.ClassCode);
            if (IsDescriptionContinuation(line))
            {
                CourseName = AppendCell(CourseName, line.CourseCode);
            }
            else
            {
                CourseCode = AppendCell(CourseCode, line.CourseCode);
            }
            CourseName = AppendCell(CourseName, line.CourseName);
            Credits = AppendCell(Credits, line.Credits);
            LtHours = AppendCell(LtHours, line.LtHours);
            ThHours = AppendCell(ThHours, line.ThHours);
            ClassSize = AppendCell(ClassSize, line.ClassSize);
            LhpCode = AppendCell(LhpCode, line.LhpCode);
            Group = AppendCell(Group, line.Group);
            SessionType = AppendCell(SessionType, line.SessionType);
            Day = AppendCell(Day, line.Day);
            Period = AppendCell(Period, line.Period);
            Room = AppendCell(Room, line.Room);
            Lecturer = AppendCell(Lecturer, line.Lecturer);
            Notes = AppendCell(Notes, line.Notes);
        }

        private static bool IsDescriptionContinuation(PdfVisualLine line) =>
            string.IsNullOrWhiteSpace(line.LhpCode) &&
            string.IsNullOrWhiteSpace(line.Group) &&
            string.IsNullOrWhiteSpace(line.SessionType) &&
            string.IsNullOrWhiteSpace(line.Day) &&
            string.IsNullOrWhiteSpace(line.Period) &&
            string.IsNullOrWhiteSpace(line.Room) &&
            string.IsNullOrWhiteSpace(line.Lecturer);

        private static string AppendClass(string current, string value)
        {
            if (string.IsNullOrWhiteSpace(value) || string.Equals(current, value, StringComparison.Ordinal))
            {
                return current;
            }

            return AppendCell(current, value);
        }

        private static string AppendCell(string current, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return current;
            }

            if (string.IsNullOrWhiteSpace(current))
            {
                return value;
            }

            return current.EndsWith('-') || value.StartsWith('-')
                ? current + value
                : current + " " + value;
        }
    }

    private sealed class PdfVisualLine
    {
        private readonly List<PdfWord> words = [];

        public PdfVisualLine(int pageNumber, double bottom)
        {
            PageNumber = pageNumber;
            Bottom = bottom;
        }

        public int PageNumber { get; }

        public double Bottom { get; }

        public string ClassCode => Text(Columns.Class);

        public string CourseCode => Text(Columns.CourseCode);

        public string CourseName => Text(Columns.CourseName);

        public string Credits => Text(Columns.Credits);

        public string LtHours => Text(Columns.LtHours);

        public string ThHours => Text(Columns.ThHours);

        public string ClassSize => Text(Columns.ClassSize);

        public string LhpCode => Text(Columns.Lhp);

        public string Group => Text(Columns.Group);

        public string SessionType => Text(Columns.SessionType);

        public string Day => Text(Columns.Day);

        public string Period => Text(Columns.Period);

        public string Room => Text(Columns.Room);

        public string Lecturer => Text(Columns.Lecturer);

        public string Notes => Text(Columns.Notes);

        public void Add(Word word, PdfColumnBands columns)
        {
            var left = word.BoundingBox.Left;
            if (left < columns.Class.Min || left >= columns.Notes.Max)
            {
                return;
            }

            words.Add(new PdfWord(word.Text, left));
        }

        private string Text((double Min, double Max) band)
        {
            var selected = words.Where(word => word.Left >= band.Min && word.Left < band.Max)
                .OrderBy(word => word.Left)
                .Select(word => word.Text.Trim())
                .Where(text => text.Length > 0);
            return Normalize(string.Join(" ", selected));
        }

        private static string Normalize(string value) =>
            value.Replace('\u00A0', ' ').Trim();
    }

    private readonly record struct PdfWord(string Text, double Left);

    private readonly record struct PdfColumnBands(
        (double Min, double Max) Class,
        (double Min, double Max) CourseCode,
        (double Min, double Max) CourseName,
        (double Min, double Max) Credits,
        (double Min, double Max) LtHours,
        (double Min, double Max) ThHours,
        (double Min, double Max) SelfStudy,
        (double Min, double Max) TeachingLoad,
        (double Min, double Max) ClassSize,
        (double Min, double Max) Lhp,
        (double Min, double Max) Group,
        (double Min, double Max) SessionType,
        (double Min, double Max) Day,
        (double Min, double Max) Period,
        (double Min, double Max) Room,
        (double Min, double Max) Lecturer,
        (double Min, double Max) Notes);
}
