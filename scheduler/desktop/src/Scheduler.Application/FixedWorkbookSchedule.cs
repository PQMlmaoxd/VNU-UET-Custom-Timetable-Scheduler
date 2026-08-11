using System.Collections.Immutable;
using Scheduler.Domain;

namespace Scheduler.Application;

/// <summary>
/// Materializes the immutable assignments already present in an imported workbook.
/// </summary>
public static class FixedWorkbookSchedule
{
    public static Schedule Create(SchedulingProblem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);

        return new Schedule(
            problem.ProblemId,
            problem.SchedulableSessions
                .Select(session => new Assignment(
                    session.SessionId,
                    session.TimeSlot ?? throw new InvalidOperationException("Schedulable session is missing a time slot."),
                    session.Room ?? throw new InvalidOperationException("Schedulable session is missing a room.")))
                .ToImmutableArray(),
            problem.OnlineSessions);
    }
}
