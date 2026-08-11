using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Scheduler.Domain;

namespace Scheduler.Application;

public enum PersonalSelectionClauseKind
{
    AtLeastOne,
    AtMostOne,
    Conflict,
}

public sealed record PersonalSelectionCnfVariable(
    int VariableId,
    int PairIndex,
    int CandidateIndex,
    PersonalSelectionChoice Choice);

public sealed record PersonalSelectionCnfClause(
    ImmutableArray<int> Literals,
    PersonalSelectionClauseKind Kind);

/// <summary>
/// A deterministic DIMACS-compatible encoding of a fixed-workbook personal selection.
/// Every variable represents one existing LHP candidate; no timetable sessions are moved.
/// </summary>
public sealed record PersonalSelectionCnf(
    ImmutableArray<PersonalSelectionCnfVariable> Variables,
    ImmutableArray<PersonalSelectionCnfClause> Clauses,
    int PairCount,
    int AtLeastOneClauseCount,
    int AtMostOneClauseCount,
    int ConflictClauseCount)
{
    public int VariableCount => Variables.Length;

    public int ClauseCount => Clauses.Length;

    /// <summary>
    /// Candidate variables grouped by the pair that requires exactly one choice.
    /// An empty value represents a generic CNF without personal-selection groups.
    /// </summary>
    public ImmutableArray<ImmutableArray<int>> ExactlyOneGroups
    {
        get
        {
            if (PairCount == 0)
            {
                return [];
            }

            var groups = ImmutableArray.CreateBuilder<ImmutableArray<int>>(PairCount);
            for (var pairIndex = 0; pairIndex < PairCount; pairIndex++)
            {
                var group = Variables
                    .Where(variable => variable.PairIndex == pairIndex)
                    .Select(variable => variable.VariableId)
                    .ToImmutableArray();
                if (group.IsEmpty)
                {
                    throw new InvalidOperationException(
                        $"Personal-selection pair {pairIndex} has no candidate variables.");
                }

                groups.Add(group);
            }

            return groups.ToImmutable();
        }
    }

    public string ToDimacs()
    {
        var content = new StringBuilder();
        content.AppendLine("c personal_sat fixed-workbook selector CNF export");
        content.AppendLine(CultureInfo.InvariantCulture, $"c pairs {PairCount}");
        content.AppendLine(CultureInfo.InvariantCulture, $"c clauses_at_least_one {AtLeastOneClauseCount}");
        content.AppendLine(CultureInfo.InvariantCulture, $"c clauses_at_most_one {AtMostOneClauseCount}");
        content.AppendLine(CultureInfo.InvariantCulture, $"c clauses_conflict {ConflictClauseCount}");
        foreach (var variable in Variables)
        {
            content.Append("c var ")
                .Append(variable.VariableId)
                .Append(" pair=").Append(variable.PairIndex)
                .Append(" candidate=").Append(variable.CandidateIndex)
                .Append(" lhp=").Append(variable.Choice.LhpCode)
                .Append(" sessions=").AppendJoin(',', variable.Choice.SessionIds)
                .AppendLine();
        }

        content.AppendLine(CultureInfo.InvariantCulture, $"p cnf {VariableCount} {ClauseCount}");
        foreach (var clause in Clauses)
        {
            foreach (var literal in clause.Literals)
            {
                content.Append(literal).Append(' ');
            }

            content.AppendLine("0");
        }

        return content.ToString();
    }
}

public static class PersonalSelectionCnfEncoder
{
    public static PersonalSelectionCnf Encode(
        SchedulingProblem problem,
        PersonalSelectionSpec selectionSpec)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(selectionSpec);

        var sessionsById = problem.Sessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        var fixedCandidates = BuildFixedCandidates(sessionsById, selectionSpec);
        var variables = ImmutableArray.CreateBuilder<PersonalSelectionCnfVariable>();
        var clauses = ImmutableArray.CreateBuilder<PersonalSelectionCnfClause>();
        var variableToCandidate = new Dictionary<int, FixedCandidate>();
        var nextVariableId = 1;
        var atLeastOneClauseCount = 0;
        var atMostOneClauseCount = 0;
        var conflictClauseCount = 0;

        foreach (var pairCandidates in fixedCandidates)
        {
            var pairVariableIds = ImmutableArray.CreateBuilder<int>();
            foreach (var candidate in pairCandidates)
            {
                variableToCandidate.Add(nextVariableId, candidate);
                variables.Add(new PersonalSelectionCnfVariable(
                    nextVariableId,
                    candidate.PairIndex,
                    candidate.CandidateIndex,
                    candidate.Choice));
                pairVariableIds.Add(nextVariableId);
                nextVariableId++;
            }

            clauses.Add(new PersonalSelectionCnfClause(
                pairVariableIds.ToImmutable(),
                PersonalSelectionClauseKind.AtLeastOne));
            atLeastOneClauseCount++;

            for (var leftIndex = 0; leftIndex < pairVariableIds.Count; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < pairVariableIds.Count; rightIndex++)
                {
                    clauses.Add(new PersonalSelectionCnfClause(
                        ImmutableArray.Create(-pairVariableIds[leftIndex], -pairVariableIds[rightIndex]),
                        PersonalSelectionClauseKind.AtMostOne));
                    atMostOneClauseCount++;
                }
            }
        }

        conflictClauseCount = AddIndexedConflictClauses(variableToCandidate, clauses);

        return new PersonalSelectionCnf(
            variables.ToImmutable(),
            clauses.ToImmutable(),
            selectionSpec.Pairs.Length,
            atLeastOneClauseCount,
            atMostOneClauseCount,
            conflictClauseCount);
    }

    private static ImmutableArray<ImmutableArray<FixedCandidate>> BuildFixedCandidates(
        Dictionary<string, Session> sessionsById,
        PersonalSelectionSpec selectionSpec) =>
        selectionSpec.Pairs
            .Select((pair, pairIndex) => pair.Candidates
                .Select((candidate, candidateIndex) => new FixedCandidate(
                    pairIndex,
                    candidateIndex,
                    new PersonalSelectionChoice(
                        pair.DesiredAssignment,
                        candidate.LhpCode,
                        candidate.SessionIds,
                        FixedSessionTimeSlots(candidate, sessionsById),
                        candidate.TeachingUnitKey)))
                .ToImmutableArray())
            .ToImmutableArray();

    private static ImmutableArray<KeyValuePair<string, TimeSlot>> FixedSessionTimeSlots(
        SelectionCandidate candidate,
        Dictionary<string, Session> sessionsById)
    {
        var sessionTimeSlots = ImmutableArray.CreateBuilder<KeyValuePair<string, TimeSlot>>();
        foreach (var sessionId in candidate.SessionIds)
        {
            if (!sessionsById.TryGetValue(sessionId, out var session) || session.TimeSlot is not { } timeSlot)
            {
                continue;
            }

            sessionTimeSlots.Add(new KeyValuePair<string, TimeSlot>(sessionId, timeSlot));
        }

        return sessionTimeSlots
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static int AddIndexedConflictClauses(
        Dictionary<int, FixedCandidate> variableToCandidate,
        ImmutableArray<PersonalSelectionCnfClause>.Builder clauses)
    {
        // Session and atomic-slot buckets are equivalent to the conflict predicate without all-pairs scans.
        var sessionIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var timeSlotIndex = new Dictionary<AtomicTimeSlot, List<int>>();
        foreach (var (variableId, candidate) in variableToCandidate)
        {
            foreach (var sessionId in candidate.Choice.SessionIds.Distinct(StringComparer.Ordinal))
            {
                AddIndexValue(sessionIndex, sessionId, variableId);
            }

            var candidateSlots = new HashSet<AtomicTimeSlot>();
            foreach (var (_, timeSlot) in candidate.Choice.SessionTimeSlots)
            {
                foreach (var period in timeSlot.Period.ToAtomicPeriods())
                {
                    candidateSlots.Add(new AtomicTimeSlot(timeSlot.Day, period));
                }
            }

            foreach (var slot in candidateSlots)
            {
                AddIndexValue(timeSlotIndex, slot, variableId);
            }
        }

        var conflicts = new HashSet<ConflictPair>();
        AddBucketConflicts(sessionIndex.Values, variableToCandidate, conflicts);
        AddBucketConflicts(timeSlotIndex.Values, variableToCandidate, conflicts);

        foreach (var conflict in conflicts.OrderBy(pair => pair.LeftVariableId).ThenBy(pair => pair.RightVariableId))
        {
            clauses.Add(new PersonalSelectionCnfClause(
                ImmutableArray.Create(-conflict.LeftVariableId, -conflict.RightVariableId),
                PersonalSelectionClauseKind.Conflict));
        }

        return conflicts.Count;
    }

    private static void AddIndexValue<TKey>(Dictionary<TKey, List<int>> index, TKey key, int variableId)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out var variableIds))
        {
            variableIds = [];
            index.Add(key, variableIds);
        }

        variableIds.Add(variableId);
    }

    private static void AddBucketConflicts(
        IEnumerable<List<int>> buckets,
        Dictionary<int, FixedCandidate> variableToCandidate,
        HashSet<ConflictPair> conflicts)
    {
        foreach (var bucket in buckets)
        {
            for (var leftIndex = 0; leftIndex < bucket.Count; leftIndex++)
            {
                for (var rightIndex = leftIndex + 1; rightIndex < bucket.Count; rightIndex++)
                {
                    var leftVariableId = bucket[leftIndex];
                    var rightVariableId = bucket[rightIndex];
                    if (variableToCandidate[leftVariableId].PairIndex == variableToCandidate[rightVariableId].PairIndex)
                    {
                        continue;
                    }

                    conflicts.Add(new ConflictPair(
                        Math.Min(leftVariableId, rightVariableId),
                        Math.Max(leftVariableId, rightVariableId)));
                }
            }
        }
    }

    private sealed record FixedCandidate(
        int PairIndex,
        int CandidateIndex,
        PersonalSelectionChoice Choice);

    private readonly record struct AtomicTimeSlot(Day Day, int Period);

    private readonly record struct ConflictPair(int LeftVariableId, int RightVariableId);
}
