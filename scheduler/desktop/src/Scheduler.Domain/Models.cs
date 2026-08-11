using System.Collections.Immutable;

namespace Scheduler.Domain;

public sealed record Lecturer
{
    public Lecturer(string name, LecturerType lecturerType = LecturerType.Individual)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        LecturerType = DomainConstants.OrganizationalUnits.Contains(name)
            ? LecturerType.Organization
            : lecturerType;
    }

    public string Name { get; }

    public LecturerType LecturerType { get; }

    public bool IsIndividual => LecturerType == LecturerType.Individual;

    public override string ToString() => Name;
}

public sealed record Room
{
    public Room(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }

    public string Building
    {
        get
        {
            var separator = Code.LastIndexOf('-');
            return separator < 0 ? Code : Code[(separator + 1)..];
        }
    }

    public bool IsVirtual => DomainConstants.VirtualRooms.Contains(Code);

    public bool IsOnline => Code == "ONL";

    public bool IsLab =>
        Building == "A" ||
        Code.StartsWith("PTN", StringComparison.Ordinal) ||
        Code is "Xưởng Cơ khí" or "Viện Di truyền";

    public string MovementZone => RoomMovement.MovementZoneForBuilding(Building);

    public int TransitionCostTo(Room other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return RoomMovement.TransitionCost(Code, Building, other.Code, other.Building);
    }

    public override string ToString() => Code;
}

public sealed record StudentCohort
{
    public StudentCohort(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }

    public string Year => Code.Length >= 3 ? Code[..3] : Code;

    public string DepartmentLetter
    {
        get
        {
            foreach (var character in Code)
            {
                if (char.IsLetter(character) && character != 'K')
                {
                    return character.ToString();
                }
            }

            return "?";
        }
    }

    public override string ToString() => Code;
}

public sealed record Course(string Code, string Name, int Credits, int LtHours = 0, int ThHours = 0)
{
    public override string ToString() => $"{Code} - {Name}";
}

public sealed record TimeSlot(Day Day, Period Period)
{
    public bool OverlapsWith(TimeSlot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Day == other.Day && Period.OverlapsWith(other.Period);
    }

    public override string ToString() => $"{DayName(Day)}-Ca{Period.ToWorkbookValue()}";

    private static string DayName(Day day) => day switch
    {
        Day.Monday => "Mon",
        Day.Tuesday => "Tue",
        Day.Wednesday => "Wed",
        Day.Thursday => "Thu",
        Day.Friday => "Fri",
        Day.Saturday => "Sat",
        _ => "?",
    };
}

public sealed record Session
{
    public Session(
        string sessionId,
        string lhpCode,
        Course course,
        SessionType sessionType,
        string group,
        int classSize,
        ImmutableArray<Lecturer> lecturers,
        ImmutableArray<StudentCohort> studentCohorts,
        TimeSlot? timeSlot = null,
        Room? room = null,
        string note = "",
        int sourceRow = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(lhpCode);
        ArgumentNullException.ThrowIfNull(course);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        if (sessionType == SessionType.Onl && room is not null && !room.IsVirtual)
        {
            throw new DomainValidationException(
                $"Session {sessionId}: ONL session must use a virtual room if one is set.");
        }

        if (sessionType.RequiresPhysicalRoom() && (timeSlot is null || room is null))
        {
            throw new DomainValidationException(
                $"Session {sessionId}: non-ONL session must have both timeslot and room.");
        }

        if (lecturers.IsDefaultOrEmpty)
        {
            throw new DomainValidationException($"Session {sessionId}: must have at least one lecturer.");
        }

        if (studentCohorts.IsDefaultOrEmpty)
        {
            throw new DomainValidationException($"Session {sessionId}: must have at least one cohort.");
        }

        SessionId = sessionId;
        LhpCode = lhpCode;
        Course = course;
        SessionType = sessionType;
        Group = group;
        ClassSize = classSize;
        Lecturers = lecturers;
        StudentCohorts = studentCohorts;
        TimeSlot = timeSlot;
        Room = room;
        Note = note;
        SourceRow = sourceRow;
    }

    public string SessionId { get; }

    public string LhpCode { get; }

    public Course Course { get; }

    public SessionType SessionType { get; }

    public string Group { get; }

    public int ClassSize { get; }

    public ImmutableArray<Lecturer> Lecturers { get; }

    public ImmutableArray<StudentCohort> StudentCohorts { get; }

    public TimeSlot? TimeSlot { get; }

    public Room? Room { get; }

    public string Note { get; }

    public int SourceRow { get; }

    public bool NeedsPhysicalScheduling => SessionType.RequiresPhysicalRoom();

    public ImmutableArray<Lecturer> IndividualLecturers =>
        Lecturers.Where(lecturer => lecturer.IsIndividual).ToImmutableArray();

    public string AnchorLabel => $"{Course.Code} | {string.Join('+', Lecturers.Select(lecturer => lecturer.Name))}";

    public override string ToString()
    {
        var cohorts = string.Join('+', StudentCohorts.Select(cohort => cohort.Code));
        var timeSlot = TimeSlot?.ToString() ?? "UNSCHEDULED";
        var room = Room?.ToString() ?? "NO_ROOM";
        return $"[{SessionId}] {LhpCode} {SessionType} {cohorts} @ {timeSlot} {room}";
    }
}
