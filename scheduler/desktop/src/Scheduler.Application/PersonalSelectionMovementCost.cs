using System.Collections.Immutable;
using Scheduler.Domain;

namespace Scheduler.Application;

/// <summary>
/// Scores only consecutive selected sessions on the same day. This preserves the
/// current product behavior: movement is reported after SAT feasibility, not optimized in CNF.
/// </summary>
public static class PersonalSelectionMovementCost
{
    public static int Calculate(
        SchedulingProblem problem,
        Schedule schedule,
        ImmutableArray<PersonalSelectionChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(schedule);

        var sessionsById = problem.Sessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        var selectedSessions = choices
            .SelectMany(choice => choice.SessionIds)
            .Where(sessionsById.ContainsKey)
            .Select(sessionId => sessionsById[sessionId]);
        var sessionsByDay = new Dictionary<Day, List<ResolvedSession>>();

        foreach (var session in selectedSessions)
        {
            var (timeSlot, room) = Resolve(session, schedule);
            if (timeSlot is null)
            {
                continue;
            }

            var periods = timeSlot.Period.ToAtomicPeriods();
            var resolved = new ResolvedSession(
                periods.Min(),
                periods.Max(),
                session,
                room);
            if (!sessionsByDay.TryGetValue(timeSlot.Day, out var daySessions))
            {
                daySessions = [];
                sessionsByDay.Add(timeSlot.Day, daySessions);
            }

            daySessions.Add(resolved);
        }

        var totalCost = 0;
        foreach (var daySessions in sessionsByDay.Values)
        {
            var ordered = daySessions
                .OrderBy(item => item.StartPeriod)
                .ThenBy(item => item.EndPeriod)
                .ThenBy(item => item.Session.SourceRow)
                .ThenBy(item => item.Session.SessionId, StringComparer.Ordinal)
                .ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                var previous = ordered[index - 1];
                var current = ordered[index];
                if (previous.EndPeriod + 1 == current.StartPeriod)
                {
                    totalCost += TransitionCost(previous.Session, previous.Room, current.Session, current.Room);
                }
            }
        }

        return totalCost;
    }

    private static (TimeSlot? TimeSlot, Room? Room) Resolve(Session session, Schedule schedule)
    {
        var assignment = schedule.GetAssignment(session.SessionId);
        return assignment is null
            ? (session.TimeSlot, session.Room)
            : (assignment.TimeSlot, assignment.Room);
    }

    private static int TransitionCost(Session fromSession, Room? fromRoom, Session toSession, Room? toRoom)
    {
        if (fromSession.SessionType == SessionType.Onl || toSession.SessionType == SessionType.Onl ||
            fromRoom is null || toRoom is null || fromRoom.IsOnline || toRoom.IsOnline)
        {
            return 0;
        }

        return fromRoom.TransitionCostTo(toRoom);
    }

    private sealed record ResolvedSession(int StartPeriod, int EndPeriod, Session Session, Room? Room);
}
