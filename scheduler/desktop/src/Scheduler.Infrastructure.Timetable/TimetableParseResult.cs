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
    /// Rows with blocking import issues that could not be safely quarantined.
    /// </summary>
    public ImmutableArray<string> FatalWarnings { get; init; } = [];

    /// <summary>
    /// Course/LHP offerings excluded because their physical schedule is explicitly
    /// unresolved, such as a sanctioned "Thông báo sau" marker.
    /// </summary>
    public ImmutableArray<QuarantinedOffering> QuarantinedOfferings { get; init; } = [];
}

public sealed record QuarantinedOffering(
    string CourseCode,
    string LhpCode,
    string ReasonCode,
    ImmutableArray<string> SourceLocations,
    int QuarantinedRowCount,
    int ExcludedSessionCount);
