using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Scheduler.Domain;

namespace Scheduler.Application;

/// <summary>
/// Builds selectable teaching units from a fixed workbook. Sessions in the same
/// course/LHP are joined only when their cohort sets are transitively connected.
/// This keeps a shared lecture with K1+K2 together with each cohort's exercise,
/// without merging unrelated offerings that reuse an LHP number.
/// </summary>
public sealed class TeachingUnitCatalog
{
    private TeachingUnitCatalog(ImmutableArray<TeachingUnit> units)
    {
        Units = units;
    }

    public ImmutableArray<TeachingUnit> Units { get; }

    public static TeachingUnitCatalog Create(
        SchedulingProblem problem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(problem);

        var units = ImmutableArray.CreateBuilder<TeachingUnit>();
        foreach (var lhpGroup in problem.Sessions
                     .GroupBy(session => (session.Course.Code, session.LhpCode))
                     .OrderBy(group => group.Key.Code, StringComparer.Ordinal)
                     .ThenBy(group => group.Key.LhpCode, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessions = lhpGroup
                .OrderBy(session => session.SourceRow)
                .ThenBy(session => session.SessionId, StringComparer.Ordinal)
                .ToImmutableArray();
            var visited = new bool[sessions.Length];
            var cohortIndexes = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var sessionIndex = 0; sessionIndex < sessions.Length; sessionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var cohort in sessions[sessionIndex].StudentCohorts)
                {
                    if (!cohortIndexes.TryGetValue(cohort.Code, out var indexes))
                    {
                        indexes = [];
                        cohortIndexes.Add(cohort.Code, indexes);
                    }

                    indexes.Add(sessionIndex);
                }
            }

            for (var start = 0; start < sessions.Length; start++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (visited[start])
                {
                    continue;
                }

                var componentIndexes = new List<int>();
                var knownCohorts = new HashSet<string>(StringComparer.Ordinal);
                var queue = new Queue<int>();
                queue.Enqueue(start);
                visited[start] = true;

                while (queue.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = queue.Dequeue();
                    componentIndexes.Add(index);
                    foreach (var cohort in sessions[index].StudentCohorts)
                    {
                        knownCohorts.Add(cohort.Code);
                        if (!cohortIndexes.TryGetValue(cohort.Code, out var matchingIndexes))
                        {
                            continue;
                        }

                        foreach (var candidateIndex in matchingIndexes)
                        {
                            if (visited[candidateIndex])
                            {
                                continue;
                            }

                            visited[candidateIndex] = true;
                            queue.Enqueue(candidateIndex);
                        }
                    }
                }

                var componentSessions = componentIndexes
                    .Select(index => sessions[index])
                    .OrderBy(session => session.SourceRow)
                    .ThenBy(session => session.SessionId, StringComparer.Ordinal)
                    .ToImmutableArray();
                var cohortCodes = componentSessions
                    .SelectMany(session => session.StudentCohorts)
                    .Select(cohort => cohort.Code)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(code => code, StringComparer.Ordinal)
                    .ToImmutableArray();
                var teamMembers = componentSessions
                    .SelectMany(session => session.IndividualLecturers)
                    .Select(lecturer => lecturer.Name)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToImmutableArray();

                if (teamMembers.IsEmpty)
                {
                    continue;
                }

                var cohortSignature = cohortCodes.IsEmpty
                    ? $"sessions:{string.Join(",", componentSessions.Select(session => session.SessionId))}"
                    : string.Join("\u001F", cohortCodes);
                var teamSignature = string.Join("\u001F", teamMembers);
                units.Add(new TeachingUnit(
                    BuildUnitKey(lhpGroup.Key.Code, lhpGroup.Key.LhpCode, cohortSignature),
                    lhpGroup.Key.Code,
                    lhpGroup.Key.LhpCode,
                    BuildTeamKey(lhpGroup.Key.Code, teamSignature),
                    string.Join(" + ", teamMembers),
                    teamMembers,
                    cohortCodes,
                    componentSessions));
            }
        }

        return new TeachingUnitCatalog(units
            .OrderBy(unit => unit.CourseCode, StringComparer.Ordinal)
            .ThenBy(unit => unit.TeachingTeamLabel, StringComparer.Ordinal)
            .ThenBy(unit => unit.LhpCode, StringComparer.Ordinal)
            .ThenBy(unit => unit.Key, StringComparer.Ordinal)
            .ToImmutableArray());
    }

    public ImmutableArray<TeachingUnit> FindUnits(string courseCode, string teachingTeamKey, string teachingTeamLabel)
    {
        var byCourse = Units.Where(unit => string.Equals(unit.CourseCode, courseCode, StringComparison.Ordinal));
        var matched = string.IsNullOrWhiteSpace(teachingTeamKey)
            ? byCourse.Where(unit => string.Equals(unit.TeachingTeamLabel, teachingTeamLabel, StringComparison.Ordinal))
            : byCourse.Where(unit => string.Equals(unit.TeachingTeamKey, teachingTeamKey, StringComparison.Ordinal));
        return matched.ToImmutableArray();
    }

    private static string BuildUnitKey(string courseCode, string lhpCode, string cohortSignature) =>
        $"unit:{Sha256Hex($"{courseCode}\u001F{lhpCode}\u001F{cohortSignature}")}";

    private static string BuildTeamKey(string courseCode, string teamSignature) =>
        $"team:{Sha256Hex($"{courseCode}\u001F{teamSignature}")}";

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record TeachingUnit(
    string Key,
    string CourseCode,
    string LhpCode,
    string TeachingTeamKey,
    string TeachingTeamLabel,
    ImmutableArray<string> TeachingTeamMembers,
    ImmutableArray<string> CohortCodes,
    ImmutableArray<Session> Sessions)
{
    public ImmutableArray<string> SessionIds => Sessions.Select(session => session.SessionId).ToImmutableArray();
}
