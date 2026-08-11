using System.Collections.Immutable;

namespace Scheduler.Domain;

public sealed record LecturerConstraint(Lecturer Lecturer, TimeSlot BlockedTimeSlot, string Reason = "");

public sealed record DesiredAnchorAssignment(
    string CourseCode,
    string LecturerName,
    string CourseName = "",
    string TeachingTeamKey = "",
    string TeachingTeamLabel = "")
{
    public string DisplayTeachingTeam => string.IsNullOrWhiteSpace(TeachingTeamLabel)
        ? LecturerName
        : TeachingTeamLabel;
}

public sealed record SchedulingProblem(
    string ProblemId,
    string Department,
    string Semester,
    ImmutableArray<Session> Sessions,
    ImmutableArray<TimeSlot> AvailableTimeSlots,
    ImmutableArray<Room> AvailableRooms,
    ImmutableArray<LecturerConstraint> LecturerBlocks)
{
    public ImmutableArray<Session> SchedulableSessions =>
        Sessions.Where(session => session.NeedsPhysicalScheduling).ToImmutableArray();

    public ImmutableArray<Session> OnlineSessions =>
        Sessions.Where(session => !session.NeedsPhysicalScheduling).ToImmutableArray();

    public override string ToString() =>
        $"SchedulingProblem({Department}, {Semester}, {Sessions.Length} sessions, " +
        $"{SchedulableSessions.Length} schedulable)";
}
