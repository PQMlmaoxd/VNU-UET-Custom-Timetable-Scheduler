using System.Collections.Immutable;
using Scheduler.Domain;
using Xunit;

namespace Scheduler.Domain.Tests;

public sealed class ModelTests
{
    [Fact]
    public void KnownOrganizationCannotBeMarkedAsIndividual()
    {
        var lecturer = new Lecturer("Viện Khảo thí", LecturerType.Individual);

        Assert.Equal(LecturerType.Organization, lecturer.LecturerType);
        Assert.False(lecturer.IsIndividual);
    }

    [Theory]
    [InlineData("101-A", "202-A", RoomMovement.SameBuildingCost)]
    [InlineData("101-A", "202-B", RoomMovement.SameZoneCost)]
    [InlineData("202-G2", "404-E5", RoomMovement.SameZoneCost)]
    [InlineData("101-A", "202-T", RoomMovement.CrossZoneCost)]
    [InlineData("101-A", "101-A", RoomMovement.SameRoomCost)]
    public void TransitionCostMatchesExistingMovementRules(string from, string to, int expected)
    {
        Assert.Equal(expected, new Room(from).TransitionCostTo(new Room(to)));
    }

    [Theory]
    [InlineData("A", "GD4")]
    [InlineData("B", "GD4")]
    [InlineData("T", "GD3")]
    [InlineData("GĐ3", "GD3")]
    [InlineData("G2", "GD2")]
    [InlineData("E5", "GD2")]
    [InlineData("T5 ĐHKHTN", "ĐHKHTN")]
    [InlineData("Unknown", "Unknown")]
    public void MovementZoneMatchesBuildingClassification(string building, string expectedZone)
    {
        Assert.Equal(expectedZone, RoomMovement.MovementZoneForBuilding(building));
    }

    [Fact]
    public void RoomRecognizesVirtualAndLabSemantics()
    {
        Assert.True(new Room("ONL").IsVirtual);
        Assert.True(new Room("ONL").IsOnline);
        Assert.True(new Room("503-A").IsLab);
        Assert.True(new Room("PTN Hòa Lạc").IsLab);
        Assert.False(new Room("407-B").IsLab);
    }

    [Fact]
    public void SessionRejectsPhysicalSessionWithoutRoomOrTimeslot()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => CreateSession(timeSlot: null, useDefaultTimeSlot: false));

        Assert.Contains("non-ONL session must have both timeslot and room", exception.Message);
    }

    [Fact]
    public void SessionAllowsOnlineSessionWithFixedTimeslotAndVirtualRoom()
    {
        var session = CreateSession(
            sessionType: SessionType.Onl,
            timeSlot: new TimeSlot(Day.Monday, Period.Ca2),
            room: new Room("ONL"));

        Assert.False(session.NeedsPhysicalScheduling);
        Assert.Equal("Mon-Ca2", session.TimeSlot?.ToString());
    }

    [Fact]
    public void SessionFiltersOrganizationalLecturers()
    {
        var session = CreateSession(
            lecturers: ImmutableArray.Create(new Lecturer("Nguyễn Hoài Sơn"), new Lecturer("Viện Khảo thí")));

        var lecturer = Assert.Single(session.IndividualLecturers);
        Assert.Equal("Nguyễn Hoài Sơn", lecturer.Name);
    }

    private static Session CreateSession(
        SessionType sessionType = SessionType.Lt,
        TimeSlot? timeSlot = null,
        Room? room = null,
        ImmutableArray<Lecturer>? lecturers = null,
        bool useDefaultTimeSlot = true)
    {
        return new Session(
            sessionId: "test_1",
            lhpCode: "INT2213 1",
            course: new Course("INT2213", "Mạng máy tính", 4, 45, 30),
            sessionType: sessionType,
            group: "CL",
            classSize: 60,
            lecturers: lecturers ?? ImmutableArray.Create(new Lecturer("Nguyễn Hoài Sơn")),
            studentCohorts: ImmutableArray.Create(new StudentCohort("K69I-IT1")),
            timeSlot: useDefaultTimeSlot ? timeSlot ?? new TimeSlot(Day.Friday, Period.Ca4) : timeSlot,
            room: room ?? new Room("105-B"));
    }
}
