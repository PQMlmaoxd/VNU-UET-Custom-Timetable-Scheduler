using System.Collections.Immutable;
using Scheduler.Application;
using Scheduler.Domain;
using Xunit;

namespace Scheduler.Application.Tests;

public sealed class PersonalSelectionMovementCostTests
{
    [Theory]
    [InlineData("101-A", "101-A", RoomMovement.SameRoomCost)]
    [InlineData("101-B", "205-B", RoomMovement.SameBuildingCost)]
    [InlineData("101-A", "101-B", RoomMovement.SameZoneCost)]
    [InlineData("202-G2", "404-E5", RoomMovement.SameZoneCost)]
    [InlineData("101-A", "101-T", RoomMovement.CrossZoneCost)]
    [InlineData("101-T", "803-T5 ĐHKHTN", RoomMovement.CrossZoneCost)]
    public void CalculateUsesTheRoomTransitionMatrix(
        string fromRoomCode,
        string toRoomCode,
        int expectedCost)
    {
        var cost = CalculateForTwoAdjacentSessions(
            new Room(fromRoomCode),
            new Room(toRoomCode));

        Assert.Equal(expectedCost, cost);
    }

    [Fact]
    public void CalculateChargesOnlyAdjacentPhysicalSessions()
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var firstRoom = new Room("101-B");
        var secondRoom = new Room("205-B");
        var first = Session("first", course, lecturer, cohort, firstRoom, Period.Ca1, sourceRow: 5);
        var second = Session("second", course, lecturer, cohort, secondRoom, Period.Ca2, sourceRow: 6);
        var later = Session("later", course, lecturer, cohort, new Room("101-A"), Period.Ca4, sourceRow: 7);

        var movementCost = PersonalSelectionMovementCost.Calculate(
            Problem(first, second, later),
            FixedSchedule(first, second, later),
            Choices(course, lecturer, first, second, later));

        Assert.Equal(RoomMovement.SameBuildingCost, movementCost);
    }

    [Fact]
    public void CalculateChargesEveryAdjacentTransitionExactlyOnce()
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var first = Session("first", course, lecturer, cohort, new Room("101-A"), Period.Ca1, sourceRow: 5);
        var second = Session("second", course, lecturer, cohort, new Room("101-B"), Period.Ca2, sourceRow: 6);
        var third = Session("third", course, lecturer, cohort, new Room("101-T"), Period.Ca3, sourceRow: 7);

        var cost = PersonalSelectionMovementCost.Calculate(
            Problem(first, second, third),
            FixedSchedule(first, second, third),
            Choices(course, lecturer, first, second, third));

        Assert.Equal(RoomMovement.SameZoneCost + RoomMovement.CrossZoneCost, cost);
    }

    [Fact]
    public void CalculateDoesNotChargeSessionsOnDifferentDays()
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var first = Session("first", course, lecturer, cohort, new Room("101-A"), Period.Ca1, sourceRow: 5);
        var second = Session(
            "second",
            course,
            lecturer,
            cohort,
            new Room("101-T"),
            Period.Ca2,
            sourceRow: 6,
            day: Day.Tuesday);

        var cost = PersonalSelectionMovementCost.Calculate(
            Problem(first, second),
            FixedSchedule(first, second),
            Choices(course, lecturer, first, second));

        Assert.Equal(0, cost);
    }

    [Fact]
    public void CalculateChargesAdjacentRangePeriods()
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var first = Session(
            "first",
            course,
            lecturer,
            cohort,
            new Room("101-A"),
            Period.Ca1To2,
            sourceRow: 5);
        var second = Session(
            "second",
            course,
            lecturer,
            cohort,
            new Room("101-T"),
            Period.Ca3To4,
            sourceRow: 6);

        var cost = PersonalSelectionMovementCost.Calculate(
            Problem(first, second),
            FixedSchedule(first, second),
            Choices(course, lecturer, first, second));

        Assert.Equal(RoomMovement.CrossZoneCost, cost);
    }

    [Fact]
    public void CalculateDoesNotChargeOnlineTransitions()
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var first = Session("first", course, lecturer, cohort, new Room("101-A"), Period.Ca1, sourceRow: 5);
        var online = Session(
            "online",
            course,
            lecturer,
            cohort,
            new Room("ONL"),
            Period.Ca2,
            sourceRow: 6,
            sessionType: SessionType.Onl);
        var third = Session("third", course, lecturer, cohort, new Room("101-T"), Period.Ca3, sourceRow: 7);

        var cost = PersonalSelectionMovementCost.Calculate(
            Problem(first, online, third),
            FixedSchedule(first, online, third),
            Choices(course, lecturer, first, online, third));

        Assert.Equal(0, cost);
    }

    [Fact]
    public void CalculateSkipsOnlineSessionsWithoutAFixedTime()
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var physical = Session("physical", course, lecturer, cohort, new Room("101-A"), Period.Ca1, sourceRow: 5);
        var online = Session(
            "online",
            course,
            lecturer,
            cohort,
            new Room("ONL"),
            Period.Ca2,
            sourceRow: 6,
            sessionType: SessionType.Onl,
            useFixedTime: false);
        var problem = Problem(physical, online);
        var schedule = new Schedule(
            problem.ProblemId,
            ImmutableArray.Create(new Assignment(physical.SessionId, physical.TimeSlot!, physical.Room!)),
            ImmutableArray.Create(online));

        var cost = PersonalSelectionMovementCost.Calculate(
            problem,
            schedule,
            Choices(course, lecturer, physical, online));

        Assert.Equal(0, cost);
    }

    [Fact]
    public void CalculateUsesScheduleOverridesInsteadOfOriginalRoomAndTime()
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var first = Session("first", course, lecturer, cohort, new Room("101-A"), Period.Ca1, sourceRow: 5);
        var second = Session("second", course, lecturer, cohort, new Room("101-A"), Period.Ca3, sourceRow: 6);
        var problem = Problem(first, second);
        var schedule = new Schedule(
            problem.ProblemId,
            ImmutableArray.Create(
                new Assignment(first.SessionId, new TimeSlot(Day.Monday, Period.Ca1), new Room("101-T")),
                new Assignment(second.SessionId, new TimeSlot(Day.Monday, Period.Ca2), new Room("101-A"))),
            ImmutableArray<Session>.Empty);

        var cost = PersonalSelectionMovementCost.Calculate(
            problem,
            schedule,
            Choices(course, lecturer, first, second));

        Assert.Equal(RoomMovement.CrossZoneCost, cost);
    }

    private static int CalculateForTwoAdjacentSessions(Room firstRoom, Room secondRoom)
    {
        var course = new Course("INT2213", "Networks", 4);
        var lecturer = new Lecturer("Lecturer A");
        var cohort = new StudentCohort("K69I-IT1");
        var first = Session("first", course, lecturer, cohort, firstRoom, Period.Ca1, sourceRow: 5);
        var second = Session("second", course, lecturer, cohort, secondRoom, Period.Ca2, sourceRow: 6);

        return PersonalSelectionMovementCost.Calculate(
            Problem(first, second),
            FixedSchedule(first, second),
            Choices(course, lecturer, first, second));
    }

    private static SchedulingProblem Problem(params Session[] sessions) => new(
        "test",
        "CNTT",
        "test",
        sessions.ToImmutableArray(),
        ImmutableArray<TimeSlot>.Empty,
        sessions.Select(session => session.Room).OfType<Room>().Distinct().ToImmutableArray(),
        ImmutableArray<LecturerConstraint>.Empty);

    private static Schedule FixedSchedule(params Session[] sessions) => new(
        "test",
        sessions.Select(session => new Assignment(session.SessionId, session.TimeSlot!, session.Room!))
            .ToImmutableArray(),
        ImmutableArray<Session>.Empty);

    private static ImmutableArray<PersonalSelectionChoice> Choices(
        Course course,
        Lecturer lecturer,
        params Session[] sessions) => ImmutableArray.Create(new PersonalSelectionChoice(
        new DesiredAnchorAssignment(course.Code, lecturer.Name),
        "LHP-A",
        sessions.Select(session => session.SessionId).ToImmutableArray(),
        ImmutableArray<KeyValuePair<string, TimeSlot>>.Empty));

    private static Session Session(
        string sessionId,
        Course course,
        Lecturer lecturer,
        StudentCohort cohort,
        Room room,
        Period period,
        int sourceRow,
        Day day = Day.Monday,
        SessionType sessionType = SessionType.Lt,
        bool useFixedTime = true) => new(
        sessionId,
        "LHP-A",
        course,
        sessionType,
        "CL",
        60,
        ImmutableArray.Create(lecturer),
        ImmutableArray.Create(cohort),
        useFixedTime ? new TimeSlot(day, period) : null,
        room,
        sourceRow: sourceRow);
}
