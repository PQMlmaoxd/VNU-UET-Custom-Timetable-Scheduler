using System.Collections.Immutable;
using Scheduler.Application;
using Scheduler.Domain;
using Xunit;

namespace Scheduler.Application.Tests;

public sealed class PersonalSelectionBuilderTests
{
    private static readonly Course Networks = new("INT2213", "Networks", 4);
    private static readonly Course SoftwareEngineering = new("INT2208", "Software Engineering", 3);
    private static readonly Lecturer LecturerA = new("Nguyễn Hoài Sơn");
    private static readonly Lecturer LecturerB = new("Đào Minh Thư");
    private static readonly Lecturer Organization = new("Viện Khảo thí");
    private static readonly StudentCohort Cohort = new("K69I-IT1");
    private static readonly Room Room = new("105-B");
    private static readonly TimeSlot Monday1 = new(Day.Monday, Period.Ca1);

    [Fact]
    public void BuildGroupsMatchingSessionsByLhpAndOrdersThemBySourceRowThenId()
    {
        var problem = Problem(
            Session("row_30", "LHP-B", Networks, LecturerA, 30),
            Session("row_20", "LHP-A", Networks, LecturerA, 20),
            Session("row_10", "LHP-A", Networks, LecturerA, 10),
            Session("row_11", "LHP-A", Networks, LecturerA, 10),
            Session("row_40", "LHP-C", Networks, LecturerB, 40),
            Session("row_50", "LHP-D", SoftwareEngineering, LecturerA, 50));

        var specification = PersonalSelectionBuilder.Build(
            problem,
            ImmutableArray.Create(new DesiredAnchorAssignment("INT2213", LecturerA.Name, "Requested name")));

        var pair = Assert.Single(specification.Pairs);
        Assert.Equal("Requested name", pair.DesiredAssignment.CourseName);
        Assert.Collection(
            pair.Candidates,
            candidate =>
            {
                Assert.Equal("LHP-A", candidate.LhpCode);
                Assert.Equal("row_10,row_11,row_20", string.Join(',', candidate.SessionIds));
            },
            candidate =>
            {
                Assert.Equal("LHP-B", candidate.LhpCode);
                Assert.Equal("row_30", string.Join(',', candidate.SessionIds));
            });
    }

    [Fact]
    public void BuildIncludesOnlineSessionsAndExcludesOrganizationLecturers()
    {
        var online = new Session(
            "row_10",
            "LHP-ONL",
            Networks,
            SessionType.Onl,
            "CL",
            60,
            ImmutableArray.Create(LecturerA),
            ImmutableArray.Create(Cohort),
            Monday1,
            new Room("ONL"),
            sourceRow: 10);
        var organizationOnly = Session("row_11", "LHP-ORG", Networks, Organization, 11);
        var problem = Problem(online, organizationOnly);

        var specification = PersonalSelectionBuilder.Build(
            problem,
            ImmutableArray.Create(new DesiredAnchorAssignment(Networks.Code, LecturerA.Name)));

        var candidate = Assert.Single(Assert.Single(specification.Pairs).Candidates);
        Assert.Equal("LHP-ONL", candidate.LhpCode);
        Assert.Equal(online.SessionId, Assert.Single(candidate.SessionIds));

        var exception = Assert.Throws<ArgumentException>(() => PersonalSelectionBuilder.Build(
            problem,
            ImmutableArray.Create(new DesiredAnchorAssignment(Networks.Code, Organization.Name))));
        Assert.Contains("does not teach", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRejectsDuplicateRequestedPairs()
    {
        var problem = Problem(Session("row_10", "LHP-A", Networks, LecturerA, 10));
        var request = new DesiredAnchorAssignment(Networks.Code, LecturerA.Name);

        var exception = Assert.Throws<ArgumentException>(() => PersonalSelectionBuilder.Build(
            problem,
            ImmutableArray.Create(request, request)));

        Assert.Contains("appears more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildBundlesCoTaughtSessionsAcrossTransitivelyConnectedCohorts()
    {
        var cohortOne = new StudentCohort("K1");
        var cohortTwo = new StudentCohort("K2");
        var lecture = new Session(
            "lt",
            "3",
            Networks,
            SessionType.Lt,
            "CL",
            60,
            ImmutableArray.Create(LecturerA),
            ImmutableArray.Create(cohortOne, cohortTwo),
            Monday1,
            Room,
            sourceRow: 10);
        var exerciseOne = new Session(
            "bt-k1",
            "3",
            Networks,
            SessionType.Bt,
            "CL",
            30,
            ImmutableArray.Create(LecturerB),
            ImmutableArray.Create(cohortOne),
            new TimeSlot(Day.Tuesday, Period.Ca2),
            Room,
            sourceRow: 11);
        var exerciseTwo = new Session(
            "bt-k2",
            "3",
            Networks,
            SessionType.Bt,
            "CL",
            30,
            ImmutableArray.Create(LecturerB),
            ImmutableArray.Create(cohortTwo),
            new TimeSlot(Day.Wednesday, Period.Ca3),
            Room,
            sourceRow: 12);
        var problem = Problem(lecture, exerciseOne, exerciseTwo);

        var unit = Assert.Single(TeachingUnitCatalog.Create(problem).Units);
        Assert.Equal("Nguyễn Hoài Sơn + Đào Minh Thư", unit.TeachingTeamLabel);
        Assert.Equal("lt,bt-k1,bt-k2", string.Join(',', unit.SessionIds));

        var specification = PersonalSelectionBuilder.Build(
            problem,
            ImmutableArray.Create(new DesiredAnchorAssignment(
                Networks.Code,
                unit.TeachingTeamLabel,
                Networks.Name,
                unit.TeachingTeamKey,
                unit.TeachingTeamLabel)));
        var candidate = Assert.Single(Assert.Single(specification.Pairs).Candidates);
        Assert.Equal("lt,bt-k1,bt-k2", string.Join(',', candidate.SessionIds));
        Assert.Equal(unit.Key, candidate.TeachingUnitKey);
    }

    [Fact]
    public void TeachingUnitsDoNotMergeSeparateCohortComponents()
    {
        var first = new StudentCohort("K1");
        var second = new StudentCohort("K2");
        var problem = Problem(
            new Session("k1", "3", Networks, SessionType.Lt, "CL", 30, ImmutableArray.Create(LecturerA), ImmutableArray.Create(first), Monday1, Room, sourceRow: 10),
            new Session("k2", "3", Networks, SessionType.Lt, "CL", 30, ImmutableArray.Create(LecturerB), ImmutableArray.Create(second), new TimeSlot(Day.Tuesday, Period.Ca1), Room, sourceRow: 11));

        var units = TeachingUnitCatalog.Create(problem).Units;
        Assert.Equal(2, units.Length);
        Assert.All(units, unit => Assert.Single(unit.SessionIds));
    }

    [Fact]
    public void CoTaughtExerciseOccupancyCreatesCrossCourseConflictClause()
    {
        var cohort = new StudentCohort("K1");
        var secondLecturer = new Lecturer("Lê Thanh Hà");
        var firstProblem = new Session(
            "lecture",
            "3",
            Networks,
            SessionType.Lt,
            "CL",
            60,
            ImmutableArray.Create(LecturerA),
            ImmutableArray.Create(cohort),
            Monday1,
            Room,
            sourceRow: 10);
        var exercise = new Session(
            "exercise",
            "3",
            Networks,
            SessionType.Bt,
            "CL",
            30,
            ImmutableArray.Create(LecturerB),
            ImmutableArray.Create(cohort),
            new TimeSlot(Day.Tuesday, Period.Ca2),
            Room,
            sourceRow: 11);
        var conflictingCourse = new Session(
            "other",
            "1",
            SoftwareEngineering,
            SessionType.Lt,
            "CL",
            60,
            ImmutableArray.Create(secondLecturer),
            ImmutableArray.Create(new StudentCohort("K9")),
            new TimeSlot(Day.Tuesday, Period.Ca2),
            Room,
            sourceRow: 12);
        var problem = Problem(firstProblem, exercise, conflictingCourse);
        var units = TeachingUnitCatalog.Create(problem).Units;
        var networkUnit = units.Single(unit => unit.CourseCode == Networks.Code);
        var engineeringUnit = units.Single(unit => unit.CourseCode == SoftwareEngineering.Code);

        var prepared = PersonalSelectionPreparation.Create(
            problem,
            ImmutableArray.Create(
                new DesiredAnchorAssignment(Networks.Code, networkUnit.TeachingTeamLabel, Networks.Name, networkUnit.TeachingTeamKey, networkUnit.TeachingTeamLabel),
                new DesiredAnchorAssignment(SoftwareEngineering.Code, engineeringUnit.TeachingTeamLabel, SoftwareEngineering.Name, engineeringUnit.TeachingTeamKey, engineeringUnit.TeachingTeamLabel)));

        Assert.Equal(1, prepared.Cnf.ConflictClauseCount);
    }

    [Theory]
    [InlineData("CNTT", "in the CNTT department")]
    [InlineData("ALL", "in the workbook")]
    public void BuildReportsTheCompatibilityScopeForUnknownPairs(string department, string expectedScope)
    {
        var problem = Problem(new[] { Session("row_10", "LHP-A", Networks, LecturerA, 10) }, department);

        var exception = Assert.Throws<ArgumentException>(() => PersonalSelectionBuilder.Build(
            problem,
            ImmutableArray.Create(new DesiredAnchorAssignment(Networks.Code, LecturerB.Name))));

        Assert.Contains($"Teaching team '{LecturerB.Name}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(expectedScope, exception.Message, StringComparison.Ordinal);
    }

    private static SchedulingProblem Problem(params Session[] sessions) => Problem(sessions, "CNTT");

    private static SchedulingProblem Problem(Session[] sessions, string department) =>
        new(
            "test",
            department,
            "test",
            sessions.ToImmutableArray(),
            ImmutableArray.Create(Monday1),
            ImmutableArray.Create(Room),
            ImmutableArray<LecturerConstraint>.Empty);

    private static Session Session(string id, string lhpCode, Course course, Lecturer lecturer, int sourceRow) =>
        new(
            id,
            lhpCode,
            course,
            SessionType.Lt,
            "CL",
            60,
            ImmutableArray.Create(lecturer),
            ImmutableArray.Create(Cohort),
            Monday1,
            Room,
            sourceRow: sourceRow);
}
