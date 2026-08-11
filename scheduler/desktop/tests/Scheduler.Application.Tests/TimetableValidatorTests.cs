using System.Collections.Immutable;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure.Xlsx;
using Scheduler.Infrastructure.Timetable;
using Xunit;
using Xunit.Sdk;

namespace Scheduler.Application.Tests;

public sealed class TimetableValidatorTests
{
    private static readonly Course CourseA = new("INT2213", "Networks", 4, 45, 30);
    private static readonly Course CourseB = new("INT2208", "Software Engineering", 3, 30, 30);
    private static readonly Lecturer LecturerA = new("Nguyễn Hoài Sơn");
    private static readonly Lecturer LecturerB = new("Đào Minh Thư");
    private static readonly Lecturer Organization = new("Viện Khảo thí");
    private static readonly StudentCohort CohortA = new("K69I-IT1");
    private static readonly StudentCohort CohortB = new("K69I-IT2");
    private static readonly Room Room105B = new("105-B");
    private static readonly Room Room407A = new("407-A");
    private static readonly Room Room503A = new("503-A");
    private static readonly Room OnlineRoom = new("ONL");
    private static readonly TimeSlot Monday1 = new(Day.Monday, Period.Ca1);
    private static readonly TimeSlot Monday2 = new(Day.Monday, Period.Ca2);

    [Fact]
    public void ValidateAcceptsCompleteNonConflictingSchedule()
    {
        var first = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var second = Session("s2", "LHP2", CourseB, SessionType.Lt, LecturerB, CohortB, Monday1, Room407A);
        var problem = Problem(first, second);
        var schedule = Schedule(first, second, Assignment(first, Monday1, Room105B), Assignment(second, Monday1, Room407A));

        var result = TimetableValidator.Validate(schedule, problem);

        Assert.True(result.IsComplete);
        Assert.True(result.IsValid);
        Assert.Empty(result.HardViolations);
    }

    [Fact]
    public void ValidateReportsRoomLecturerCohortAndLhpConflicts()
    {
        var first = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var second = Session("s2", "LHP1", CourseB, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var problem = Problem(first, second);
        var schedule = Schedule(first, second, Assignment(first, Monday1, Room105B), Assignment(second, Monday1, Room105B));

        var result = TimetableValidator.Validate(schedule, problem);

        AssertViolation(result, "HC-1");
        AssertViolation(result, "HC-2");
        AssertViolation(result, "HC-3");
        AssertViolation(result, "HC-5");
    }

    [Fact]
    public void ValidateReportsExternalBlockAndLabCompatibility()
    {
        var session = Session("s1", "LHP1", CourseA, SessionType.Th, LecturerA, CohortA, Monday1, Room503A);
        var problem = Problem(
            ImmutableArray.Create(session),
            ImmutableArray.Create(new LecturerConstraint(LecturerA, Monday1, "Other department")));
        var schedule = Schedule(session, Assignment(session, Monday1, Room105B));

        var result = TimetableValidator.Validate(schedule, problem);

        AssertViolation(result, "HC-4");
        AssertViolation(result, "HC-6");
    }

    [Fact]
    public void ValidateIgnoresOrganizationLecturersForOverlapAndBlocks()
    {
        var first = Session("s1", "LHP1", CourseA, SessionType.Lt, Organization, CohortA, Monday1, Room105B);
        var second = Session("s2", "LHP2", CourseB, SessionType.Lt, Organization, CohortB, Monday1, Room407A);
        var problem = Problem(
            ImmutableArray.Create(first, second),
            ImmutableArray.Create(new LecturerConstraint(Organization, Monday1, "Other department")));
        var schedule = Schedule(first, second, Assignment(first, Monday1, Room105B), Assignment(second, Monday1, Room407A));

        var result = TimetableValidator.Validate(schedule, problem);

        Assert.DoesNotContain(result.HardViolations, violation => violation.ConstraintId is "HC-2" or "HC-4");
    }

    [Fact]
    public void ValidateIncludesFixedOnlineSessionsInLecturerAndCohortChecks()
    {
        var lecture = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var online = Session("s2", "LHP2", CourseB, SessionType.Onl, LecturerA, CohortA, Monday1, OnlineRoom);
        var problem = Problem(lecture, online);
        var schedule = new Schedule(
            "test",
            ImmutableArray.Create(Assignment(lecture, Monday1, Room105B)),
            OnlineSessions(lecture, online));

        var result = TimetableValidator.Validate(schedule, problem);

        AssertViolation(result, "HC-2");
        AssertViolation(result, "HC-3");
    }

    [Fact]
    public void ValidatePersonalTimetableUsesProblemOnlineSessionsWhenScheduleOmitsThem()
    {
        var online = Session("online", "LHP2", CourseB, SessionType.Onl, LecturerA, CohortA, Monday1, OnlineRoom);
        var problem = Problem(online);
        var schedule = new Schedule("test", ImmutableArray<Assignment>.Empty, ImmutableArray<Session>.Empty);
        var specification = Specification(Pair(CourseB, LecturerA, "LHP2", "online"));
        var choice = new PersonalSelectionChoice(
            new DesiredAnchorAssignment(CourseB.Code, LecturerA.Name, CourseB.Name),
            "LHP2",
            ImmutableArray.Create("online"),
            ImmutableArray<KeyValuePair<string, TimeSlot>>.Empty);

        var result = TimetableValidator.ValidatePersonalTimetable(
            schedule,
            problem,
            specification,
            ImmutableArray.Create(choice));

        AssertViolation(result, "PT-4");
    }

    [Fact]
    public void ValidateReportsMissingPhysicalAssignment()
    {
        var session = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);

        var result = TimetableValidator.Validate(Schedule(session), Problem(session));

        Assert.False(result.IsComplete);
        Assert.Contains("s1", result.MissingSessionIds);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateReportsUnknownAndDuplicateScheduleAssignmentsWithoutThrowing()
    {
        var session = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var schedule = new Schedule(
            "test",
            ImmutableArray.Create(
                Assignment(session, Monday1, Room105B),
                Assignment(session, Monday2, Room407A),
                new Assignment("unknown", Monday1, Room105B)),
            ImmutableArray<Session>.Empty);

        var result = TimetableValidator.Validate(schedule, Problem(session));

        Assert.Contains(result.HardViolations, violation => violation.ConstraintName == "duplicate_assignment_session");
        Assert.Contains(result.HardViolations, violation => violation.ConstraintName == "unknown_assignment_session");
    }

    [Fact]
    public void ValidatePersonalTimetableAcceptsValidChoices()
    {
        var first = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var second = Session("s2", "LHP2", CourseB, SessionType.Lt, LecturerB, CohortB, Monday2, Room407A);
        var problem = Problem(first, second);
        var schedule = Schedule(first, second, Assignment(first, Monday1, Room105B), Assignment(second, Monday2, Room407A));
        var spec = Specification(
            Pair(CourseA, LecturerA, "LHP1", "s1"),
            Pair(CourseB, LecturerB, "LHP2", "s2"));
        var choices = ImmutableArray.Create(
            Choice(CourseA, LecturerA, "LHP1", "s1", Monday1),
            Choice(CourseB, LecturerB, "LHP2", "s2", Monday2));

        var result = TimetableValidator.ValidatePersonalTimetable(schedule, problem, spec, choices);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidatePersonalTimetableReportsChoiceCandidateSessionAndTimeslotErrors()
    {
        var session = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var problem = Problem(session);
        var schedule = Schedule(session, Assignment(session, Monday1, Room105B));
        var spec = Specification(Pair(CourseA, LecturerA, "LHP1", "s1"));
        var choices = ImmutableArray.Create(
            Choice(CourseA, LecturerA, "UNKNOWN", "s1", Monday2));

        var result = TimetableValidator.ValidatePersonalTimetable(schedule, problem, spec, choices);

        AssertViolation(result, "PT-2");

        var wrongSession = ImmutableArray.Create(Choice(CourseA, LecturerA, "LHP1", "wrong", Monday1));
        var mismatchResult = TimetableValidator.ValidatePersonalTimetable(schedule, problem, spec, wrongSession);
        AssertViolation(mismatchResult, "PT-3");
        AssertViolation(mismatchResult, "PT-4");
    }

    [Fact]
    public void ValidatePersonalTimetableRejectsForgedCandidatesAndDuplicatePairs()
    {
        var session = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var problem = Problem(session);
        var schedule = Schedule(session, Assignment(session, Monday1, Room105B));
        var forgedPair = Pair(CourseA, LecturerA, "LHP2", "s1");
        var forgedChoice = Choice(CourseA, LecturerA, "LHP2", "s1", Monday1);

        var forgedResult = TimetableValidator.ValidatePersonalTimetable(
            schedule,
            problem,
            Specification(forgedPair),
            ImmutableArray.Create(forgedChoice));
        Assert.Contains(forgedResult.HardViolations, violation => violation.ConstraintName == "candidate_not_from_problem");

        var duplicateResult = TimetableValidator.ValidatePersonalTimetable(
            schedule,
            problem,
            Specification(Pair(CourseA, LecturerA, "LHP1", "s1"), Pair(CourseA, LecturerA, "LHP1", "s1")),
            ImmutableArray.Create(Choice(CourseA, LecturerA, "LHP1", "s1", Monday1)));
        Assert.Contains(duplicateResult.HardViolations, violation => violation.ConstraintName == "duplicate_requested_pair");
    }

    [Fact]
    public void ValidatePersonalTimetableReportsChoiceCountReuseOverlapAndUnexpectedPair()
    {
        var first = Session("s1", "LHP1", CourseA, SessionType.Lt, LecturerA, CohortA, Monday1, Room105B);
        var second = Session("s2", "LHP2", CourseB, SessionType.Lt, LecturerB, CohortB, Monday1, Room407A);
        var problem = Problem(first, second);
        var schedule = Schedule(first, second, Assignment(first, Monday1, Room105B), Assignment(second, Monday1, Room407A));
        var spec = Specification(
            Pair(CourseA, LecturerA, "LHP1", "s1"),
            Pair(CourseB, LecturerB, "LHP2", "s2"));
        var choices = ImmutableArray.Create(
            Choice(CourseA, LecturerA, "LHP1", "s1", Monday1),
            Choice(CourseB, LecturerB, "LHP2", "s1", Monday1),
            Choice(new Course("OTHER", "Other", 1), new Lecturer("Other"), "OTHER", "s2", Monday1));

        var result = TimetableValidator.ValidatePersonalTimetable(schedule, problem, spec, choices);

        AssertViolation(result, "PT-3");
        AssertViolation(result, "PT-5");
        AssertViolation(result, "PT-6");
        AssertViolation(result, "PT-7");

        var missingResult = TimetableValidator.ValidatePersonalTimetable(
            schedule,
            problem,
            spec,
            ImmutableArray<PersonalSelectionChoice>.Empty);
        AssertViolation(missingResult, "PT-1");
    }

    [Fact]
    [Trait("Category", "Compatibility")]
    public void ValidateFixedExternalWorkbookMatchesCompatibilityBaselineWhenAvailable()
    {
        var workbookPath = FindRealWorkbook();
        if (workbookPath is null)
        {
            throw SkipException.ForSkip("External XLSX fixture is not available.");
        }

        AssertFixedWorkbookValidation("ALL", 648, ("HC-1", 38), ("HC-2", 122), ("HC-3", 55), ("HC-5", 14), ("HC-6", 419));
        AssertFixedWorkbookValidation("CNTT", 81, ("HC-2", 19), ("HC-3", 2), ("HC-4", 18), ("HC-6", 42));
    }

    private static void AssertViolation(ValidationResult result, string constraintId) =>
        Assert.Contains(result.HardViolations, violation => violation.ConstraintId == constraintId);

    private static void AssertViolation(PersonalValidationResult result, string constraintId) =>
        Assert.Contains(result.HardViolations, violation => violation.ConstraintId == constraintId);

    private static void AssertFixedWorkbookValidation(
        string department,
        int expectedViolationCount,
        params (string ConstraintId, int Count)[] expectedByConstraint)
    {
        var workbookPath = FindRealWorkbook()!;
        var parseResult = TimetableParser.Parse(workbookPath, department);
        var schedule = new Schedule(
            parseResult.Problem.ProblemId,
            parseResult.Problem.SchedulableSessions
                .Select(session => new Assignment(session.SessionId, session.TimeSlot!, session.Room!))
                .ToImmutableArray(),
            parseResult.Problem.OnlineSessions);

        var result = TimetableValidator.Validate(schedule, parseResult.Problem);

        Assert.True(result.IsComplete);
        Assert.Equal(expectedViolationCount, result.ViolationCount);
        foreach (var (constraintId, count) in expectedByConstraint)
        {
            Assert.Equal(count, result.HardViolations.Count(violation => violation.ConstraintId == constraintId));
        }
    }

    private static Session Session(
        string id,
        string lhpCode,
        Course course,
        SessionType sessionType,
        Lecturer lecturer,
        StudentCohort cohort,
        TimeSlot? timeSlot,
        Room? room) =>
        new(
            id,
            lhpCode,
            course,
            sessionType,
            "CL",
            60,
            ImmutableArray.Create(lecturer),
            ImmutableArray.Create(cohort),
            timeSlot,
            room);

    private static Assignment Assignment(Session session, TimeSlot timeSlot, Room room) =>
        new(session.SessionId, timeSlot, room);

    private static SchedulingProblem Problem(params Session[] sessions) =>
        Problem(sessions.ToImmutableArray(), ImmutableArray<LecturerConstraint>.Empty);

    private static SchedulingProblem Problem(
        ImmutableArray<Session> sessions,
        ImmutableArray<LecturerConstraint> lecturerBlocks) =>
        new(
            "test",
            "CNTT",
            "test",
            sessions,
            ImmutableArray.Create(Monday1, Monday2),
            ImmutableArray.Create(Room105B, Room407A, Room503A),
            lecturerBlocks);

    private static Schedule Schedule(params Session[] sessions) =>
        new("test", ImmutableArray<Assignment>.Empty, OnlineSessions(sessions));

    private static Schedule Schedule(Session first, Session second, Assignment firstAssignment, Assignment secondAssignment) =>
        new(
            "test",
            ImmutableArray.Create(firstAssignment, secondAssignment),
            OnlineSessions(first, second));

    private static Schedule Schedule(Session session, Assignment assignment) =>
        new("test", ImmutableArray.Create(assignment), OnlineSessions(session));

    private static ImmutableArray<Session> OnlineSessions(params Session[] sessions) =>
        sessions.Where(session => session.SessionType == SessionType.Onl).ToImmutableArray();

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

    private static PersonalSelectionSpec Specification(params SelectionPairSpec[] pairs) =>
        new(pairs.ToImmutableArray());

    private static SelectionPairSpec Pair(Course course, Lecturer lecturer, string lhpCode, string sessionId) =>
        new(
            new DesiredAnchorAssignment(course.Code, lecturer.Name, course.Name),
            ImmutableArray.Create(new SelectionCandidate(lhpCode, ImmutableArray.Create(sessionId))));

    private static PersonalSelectionChoice Choice(
        Course course,
        Lecturer lecturer,
        string lhpCode,
        string sessionId,
        TimeSlot timeSlot) =>
        new(
            new DesiredAnchorAssignment(course.Code, lecturer.Name, course.Name),
            lhpCode,
            ImmutableArray.Create(sessionId),
            ImmutableArray.Create(new KeyValuePair<string, TimeSlot>(sessionId, timeSlot)));
}
