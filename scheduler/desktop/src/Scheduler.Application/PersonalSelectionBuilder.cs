using System.Collections.Immutable;
using Scheduler.Domain;

namespace Scheduler.Application;

/// <summary>
/// Creates full teaching-unit candidates for user-selected course and teaching-team pairs.
/// This is deliberately separate from validation and SAT encoding: candidates describe facts
/// from the imported workbook and do not change its schedule.
/// </summary>
public static class PersonalSelectionBuilder
{
    public static PersonalSelectionSpec Build(
        SchedulingProblem problem,
        ImmutableArray<DesiredAnchorAssignment> desiredAssignments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var catalog = TeachingUnitCatalog.Create(problem, cancellationToken);
        var pairs = desiredAssignments
            .Select(desiredAssignment => BuildPair(problem, catalog, desiredAssignment, cancellationToken))
            .ToImmutableArray();

        var requestedPairs = new HashSet<(string CourseCode, string TeachingTeamKey)>();
        foreach (var pairSpec in pairs)
        {
            var pair = (pairSpec.DesiredAssignment.CourseCode, pairSpec.DesiredAssignment.TeachingTeamKey);
            if (!requestedPairs.Add(pair))
            {
                throw new ArgumentException(
                    $"Requested course-teaching-team pair '{pair.CourseCode}' + '{pair.TeachingTeamKey}' appears more than once.",
                    nameof(desiredAssignments));
            }
        }

        return new PersonalSelectionSpec(pairs);
    }

    private static SelectionPairSpec BuildPair(
        SchedulingProblem problem,
        TeachingUnitCatalog catalog,
        DesiredAnchorAssignment desiredAssignment,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var units = catalog.FindUnits(
            desiredAssignment.CourseCode,
            desiredAssignment.TeachingTeamKey,
            desiredAssignment.DisplayTeachingTeam);
        var canonicalAssignment = units.IsEmpty
            ? desiredAssignment
            : desiredAssignment with
            {
                LecturerName = units[0].TeachingTeamLabel,
                TeachingTeamKey = units[0].TeachingTeamKey,
                TeachingTeamLabel = units[0].TeachingTeamLabel,
            };
        var candidates = units
            .OrderBy(unit => unit.LhpCode, StringComparer.Ordinal)
            .ThenBy(unit => unit.Key, StringComparer.Ordinal)
            .Select(unit => new SelectionCandidate(unit.LhpCode, unit.SessionIds, unit.Key))
            .ToImmutableArray();

        if (!candidates.IsEmpty)
        {
            return new SelectionPairSpec(canonicalAssignment, candidates);
        }

        var scopeLabel = string.Equals(problem.Department, "ALL", StringComparison.Ordinal)
            ? "in the workbook"
            : $"in the {problem.Department} department";
        throw new ArgumentException(
            $"Teaching team '{desiredAssignment.DisplayTeachingTeam}' does not teach course " +
            $"'{desiredAssignment.CourseCode}' {scopeLabel}. " +
            "The desired assignment must match an existing course-teaching-team entry in the workbook.",
            nameof(desiredAssignment));
    }
}
