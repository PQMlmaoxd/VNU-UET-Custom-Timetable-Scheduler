using System.Collections.Immutable;

namespace Scheduler.Domain;

public sealed record Assignment(string SessionId, TimeSlot TimeSlot, Room Room)
{
    public override string ToString() => $"{SessionId} -> {TimeSlot} @ {Room}";
}

public sealed record Schedule(
    string ProblemId,
    ImmutableArray<Assignment> Assignments,
    ImmutableArray<Session> OnlineSessions)
{
    public Assignment? GetAssignment(string sessionId) =>
        Assignments.FirstOrDefault(assignment => assignment.SessionId == sessionId);

    public override string ToString() => $"Schedule({ProblemId}, {Assignments.Length} assignments)";
}
