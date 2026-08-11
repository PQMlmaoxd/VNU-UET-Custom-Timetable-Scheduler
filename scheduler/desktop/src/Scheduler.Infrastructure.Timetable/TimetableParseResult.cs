using System.Collections.Immutable;
using Scheduler.Domain;

namespace Scheduler.Infrastructure.Timetable;

public sealed record TimetableParseResult(
    SchedulingProblem Problem,
    ImmutableArray<Session> OtherDepartmentSessions,
    ImmutableArray<string> Warnings,
    ImmutableArray<uint> SkippedRows,
    uint TotalRowsProcessed)
{
    /// <summary>
    /// Rows recognized as scheduled sessions with an inconsistent physical
    /// schedule. These rows must block solving.
    /// </summary>
    public ImmutableArray<string> FatalWarnings { get; init; } = [];
}
