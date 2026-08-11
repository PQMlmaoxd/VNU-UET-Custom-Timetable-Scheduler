using System.Collections.Immutable;
using Scheduler.Domain;

namespace Scheduler.Application;

public static class TimetableValidator
{
    public static ValidationResult Validate(
        Schedule schedule,
        SchedulingProblem problem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(problem);
        cancellationToken.ThrowIfCancellationRequested();

        var sessions = problem.Sessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        var assignments = new Dictionary<string, Assignment>(StringComparer.Ordinal);
        var violations = new List<ConstraintViolation>();
        foreach (var assignment in schedule.Assignments)
        {
            if (!sessions.ContainsKey(assignment.SessionId))
            {
                violations.Add(new ConstraintViolation(
                    "HC-0",
                    "unknown_assignment_session",
                    $"Schedule assigns unknown session '{assignment.SessionId}'.",
                    ImmutableArray.Create(assignment.SessionId),
                    $"session={assignment.SessionId}"));
                continue;
            }
            if (!assignments.TryAdd(assignment.SessionId, assignment))
            {
                violations.Add(new ConstraintViolation(
                    "HC-0",
                    "duplicate_assignment_session",
                    $"Schedule assigns session '{assignment.SessionId}' more than once.",
                    ImmutableArray.Create(assignment.SessionId),
                    $"session={assignment.SessionId}"));
            }
        }
        var missing = problem.SchedulableSessions
            .Where(session => !assignments.ContainsKey(session.SessionId))
            .Select(session => session.SessionId)
            .ToImmutableArray();
        violations.AddRange(CheckRoomNoOverlap(schedule, cancellationToken));
        violations.AddRange(CheckLecturerNoOverlap(schedule, sessions, assignments, problem, cancellationToken));
        violations.AddRange(CheckCohortNoOverlap(schedule, sessions, assignments, problem, cancellationToken));
        violations.AddRange(CheckLecturerExternalBlocks(schedule, sessions, problem, cancellationToken));
        violations.AddRange(CheckLhpInternal(schedule, sessions, assignments, problem, cancellationToken));
        violations.AddRange(CheckRoomCompatibility(schedule, sessions, cancellationToken));

        return new ValidationResult(missing.IsEmpty, violations.ToImmutableArray(), missing);
    }

    public static PersonalValidationResult ValidatePersonalTimetable(
        Schedule schedule,
        SchedulingProblem problem,
        PersonalSelectionSpec selectionSpec,
        ImmutableArray<PersonalSelectionChoice> choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(selectionSpec);
        cancellationToken.ThrowIfCancellationRequested();

        var sessions = problem.Sessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        var assignments = new Dictionary<string, Assignment>(StringComparer.Ordinal);
        var onlineSessionIds = problem.OnlineSessions
            .Select(session => session.SessionId)
            .ToHashSet(StringComparer.Ordinal);
        var catalog = TeachingUnitCatalog.Create(problem, cancellationToken);
        var violations = new List<ConstraintViolation>();
        foreach (var assignment in schedule.Assignments)
        {
            if (!assignments.TryAdd(assignment.SessionId, assignment))
            {
                violations.Add(new ConstraintViolation(
                    "PT-4",
                    "duplicate_schedule_assignment",
                    $"Schedule assigns session '{assignment.SessionId}' more than once.",
                    ImmutableArray.Create(assignment.SessionId),
                    $"session={assignment.SessionId}"));
            }
        }
        var expectedPairs = new Dictionary<(string CourseCode, string TeachingTeam), SelectionPairSpec>();
        foreach (var pair in selectionSpec.Pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (CourseCode: pair.DesiredAssignment.CourseCode, TeachingTeam: TeamIdentity(pair.DesiredAssignment));
            if (!expectedPairs.TryAdd(key, pair))
            {
                violations.Add(new ConstraintViolation(
                    "PT-1",
                    "duplicate_requested_pair",
                    $"Requested teaching team {key.CourseCode} + {key.TeachingTeam} appears more than once.",
                    ImmutableArray<string>.Empty,
                    $"request={key.CourseCode}::{key.TeachingTeam}"));
            }
        }

        var choicesByPair = new Dictionary<(string CourseCode, string TeachingTeam), List<PersonalSelectionChoice>>();
        foreach (var choice in choices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (CourseCode: choice.DesiredAssignment.CourseCode, TeachingTeam: TeamIdentity(choice.DesiredAssignment));
            if (!choicesByPair.TryGetValue(key, out var pairChoices))
            {
                pairChoices = [];
                choicesByPair.Add(key, pairChoices);
            }

            pairChoices.Add(choice);
        }

        foreach (var (pairKey, pairSpec) in expectedPairs)
        {
            var pairChoices = choicesByPair.GetValueOrDefault(pairKey) ?? [];
            if (pairChoices.Count != 1)
            {
                violations.Add(new ConstraintViolation(
                    "PT-1",
                    "exactly_one_lhp_per_pair",
                    $"Teaching-team request {pairKey.CourseCode} + {pairKey.TeachingTeam} must choose exactly one teaching unit, but got {pairChoices.Count}.",
                    pairChoices.SelectMany(choice => choice.SessionIds).ToImmutableArray(),
                    $"request={pairKey.CourseCode}::{pairKey.TeachingTeam} count={pairChoices.Count}"));
                continue;
            }

            var choice = pairChoices[0];
            var unknownSessionIds = choice.SessionIds
                .Where(sessionId => !sessions.ContainsKey(sessionId))
                .ToImmutableArray();
            if (!unknownSessionIds.IsEmpty)
            {
                violations.Add(new ConstraintViolation(
                    "PT-3",
                    "chosen_lhp_unknown_session",
                    $"Chosen LHP '{choice.LhpCode}' references sessions that are not in the scheduling problem.",
                    unknownSessionIds,
                    $"lhp={choice.LhpCode}"));
            }
            var candidateMatches = pairSpec.Candidates.Where(item =>
                string.Equals(item.LhpCode, choice.LhpCode, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(choice.TeachingUnitKey) ||
                 string.Equals(item.TeachingUnitKey, choice.TeachingUnitKey, StringComparison.Ordinal)))
                .Where(item => string.IsNullOrWhiteSpace(choice.TeachingUnitKey) ||
                    item.SessionIds.SequenceEqual(choice.SessionIds, StringComparer.Ordinal) ||
                    string.Equals(item.TeachingUnitKey, choice.TeachingUnitKey, StringComparison.Ordinal))
                .ToArray();
            var candidate = candidateMatches.Length == 1 ? candidateMatches[0] : null;
            if (candidate is null)
            {
                violations.Add(new ConstraintViolation(
                    "PT-2",
                    "chosen_lhp_not_in_candidates",
                    $"Chosen teaching unit '{choice.LhpCode}' is not a valid candidate for {pairKey.CourseCode} + {pairKey.TeachingTeam}.",
                    choice.SessionIds,
                    $"request={pairKey.CourseCode}::{pairKey.TeachingTeam} lhp={choice.LhpCode}"));
                continue;
            }

            if (!IsCanonicalCandidate(catalog, pairSpec.DesiredAssignment, candidate))
            {
                violations.Add(new ConstraintViolation(
                    "PT-2",
                    "candidate_not_from_problem",
                    $"Chosen teaching unit '{choice.LhpCode}' is not a canonical candidate for {pairKey.CourseCode} + {pairKey.TeachingTeam}.",
                    candidate.SessionIds,
                    $"request={pairKey.CourseCode}::{pairKey.TeachingTeam} lhp={choice.LhpCode}"));
                continue;
            }

            if (!choice.SessionIds.SequenceEqual(candidate.SessionIds))
            {
                violations.Add(new ConstraintViolation(
                    "PT-3",
                    "chosen_lhp_session_set_mismatch",
                    $"Chosen LHP '{choice.LhpCode}' does not match the expected session ids.",
                    choice.SessionIds,
                    $"lhp={choice.LhpCode}"));
            }

            var actualTimeslots = ResolveChoiceTimeSlots(choice.SessionIds, assignments, sessions, onlineSessionIds);
            if (!TimeSlotMapsEqual(actualTimeslots, choice.SessionTimeSlots))
            {
                violations.Add(new ConstraintViolation(
                    "PT-4",
                    "chosen_lhp_timeslot_mismatch",
                    $"Chosen LHP '{choice.LhpCode}' has session timeslots that do not match the solved schedule.",
                    choice.SessionIds,
                    $"lhp={choice.LhpCode}"));
            }
        }

        foreach (var (pairKey, pairChoices) in choicesByPair.Where(item => !expectedPairs.ContainsKey(item.Key)))
        {
            violations.Add(new ConstraintViolation(
                "PT-7",
                "unexpected_selected_pair",
                $"Returned personal timetable includes an unexpected teaching-team request {pairKey.CourseCode} + {pairKey.TeachingTeam}.",
                pairChoices.SelectMany(choice => choice.SessionIds).ToImmutableArray(),
                $"request={pairKey.CourseCode}::{pairKey.TeachingTeam}"));
        }

        violations.AddRange(CheckPersonalChoiceUniqueness(choices, cancellationToken));
        violations.AddRange(CheckPersonalChoiceOverlap(choices, assignments, sessions, onlineSessionIds, cancellationToken));

        return new PersonalValidationResult(
            selectionSpec.Pairs.Length,
            choices.Length,
            violations.ToImmutableArray());
    }

    private static IEnumerable<ConstraintViolation> CheckRoomNoOverlap(
        Schedule schedule,
        CancellationToken cancellationToken)
    {
        foreach (var (left, right) in OverlappingPairs(
                     schedule.Assignments.Where(assignment => !assignment.Room.IsVirtual),
                     assignment => assignment.Room.Code,
                     assignment => assignment.TimeSlot,
                     assignment => assignment.SessionId,
                     cancellationToken))
        {
            yield return new ConstraintViolation(
                "HC-1",
                "room_no_overlap",
                $"Room {left.Room.Code} double-booked: {left.SessionId} and {right.SessionId} at {left.TimeSlot}",
                ImmutableArray.Create(left.SessionId, right.SessionId),
                $"room={left.Room.Code} timeslot={left.TimeSlot}");
        }
    }

    private static bool IsCanonicalCandidate(
        TeachingUnitCatalog catalog,
        DesiredAnchorAssignment desiredAssignment,
        SelectionCandidate candidate)
    {
        var teachingTeamKey = TeamIdentity(desiredAssignment);
        var unit = catalog.Units.FirstOrDefault(item =>
            (string.IsNullOrWhiteSpace(candidate.TeachingUnitKey) ||
             string.Equals(item.Key, candidate.TeachingUnitKey, StringComparison.Ordinal)) &&
            string.Equals(item.CourseCode, desiredAssignment.CourseCode, StringComparison.Ordinal) &&
            string.Equals(item.LhpCode, candidate.LhpCode, StringComparison.Ordinal) &&
            (string.IsNullOrWhiteSpace(desiredAssignment.TeachingTeamKey)
                ? string.Equals(item.TeachingTeamLabel, teachingTeamKey, StringComparison.Ordinal)
                : string.Equals(item.TeachingTeamKey, teachingTeamKey, StringComparison.Ordinal)));
        return unit is not null && candidate.SessionIds.SequenceEqual(unit.SessionIds, StringComparer.Ordinal);
    }

    private static string TeamIdentity(DesiredAnchorAssignment assignment) =>
        string.IsNullOrWhiteSpace(assignment.TeachingTeamKey)
            ? assignment.DisplayTeachingTeam
            : assignment.TeachingTeamKey;

    private static IEnumerable<ConstraintViolation> CheckLecturerNoOverlap(
        Schedule schedule,
        IReadOnlyDictionary<string, Session> sessions,
        IReadOnlyDictionary<string, Assignment> assignments,
        SchedulingProblem problem,
        CancellationToken cancellationToken)
    {
        var slots = AssignedSessionsWithFixedOnline(schedule, sessions, assignments, problem)
            .SelectMany(item => item.Session.IndividualLecturers.Select(lecturer => (lecturer.Name, item)))
            .ToArray();

        foreach (var (left, right) in OverlappingPairs(
                     slots,
                     item => item.Name,
                     item => item.item.Assignment.TimeSlot,
                     item => item.item.Assignment.SessionId,
                     cancellationToken))
        {
            yield return new ConstraintViolation(
                "HC-2",
                "lecturer_no_overlap",
                $"Lecturer '{left.Name}' double-booked: {left.item.Assignment.SessionId} ({left.item.Session.Course.Code}) and {right.item.Assignment.SessionId} ({right.item.Session.Course.Code}) at {left.item.Assignment.TimeSlot}",
                ImmutableArray.Create(left.item.Assignment.SessionId, right.item.Assignment.SessionId),
                $"lecturer={left.Name} timeslot={left.item.Assignment.TimeSlot}");
        }
    }

    private static IEnumerable<ConstraintViolation> CheckCohortNoOverlap(
        Schedule schedule,
        IReadOnlyDictionary<string, Session> sessions,
        IReadOnlyDictionary<string, Assignment> assignments,
        SchedulingProblem problem,
        CancellationToken cancellationToken)
    {
        var slots = AssignedSessionsWithFixedOnline(schedule, sessions, assignments, problem)
            .SelectMany(item => item.Session.StudentCohorts.Select(cohort => (cohort.Code, item)))
            .ToArray();

        foreach (var (left, right) in OverlappingPairs(
                     slots,
                     item => item.Code,
                     item => item.item.Assignment.TimeSlot,
                     item => item.item.Assignment.SessionId,
                     cancellationToken))
        {
            yield return new ConstraintViolation(
                "HC-3",
                "cohort_no_overlap",
                $"Cohort '{left.Code}' double-booked: {left.item.Assignment.SessionId} ({left.item.Session.Course.Code}) and {right.item.Assignment.SessionId} ({right.item.Session.Course.Code}) at {left.item.Assignment.TimeSlot}",
                ImmutableArray.Create(left.item.Assignment.SessionId, right.item.Assignment.SessionId),
                $"cohort={left.Code} timeslot={left.item.Assignment.TimeSlot}");
        }
    }

    private static IEnumerable<ConstraintViolation> CheckLecturerExternalBlocks(
        Schedule schedule,
        Dictionary<string, Session> sessions,
        SchedulingProblem problem,
        CancellationToken cancellationToken)
    {
        var blocks = problem.LecturerBlocks
            .GroupBy(block => block.Lecturer.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(block => block.BlockedTimeSlot).ToImmutableArray(), StringComparer.Ordinal);

        foreach (var assignment in schedule.Assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sessions.TryGetValue(assignment.SessionId, out var session))
            {
                continue;
            }

            foreach (var lecturer in session.IndividualLecturers)
            {
                foreach (var blockedTimeSlot in blocks.GetValueOrDefault(lecturer.Name, []))
                {
                    if (assignment.TimeSlot.OverlapsWith(blockedTimeSlot))
                    {
                        yield return new ConstraintViolation(
                            "HC-4",
                            "lecturer_external_blocks",
                            $"Lecturer '{lecturer.Name}' scheduled at {assignment.TimeSlot} but has external block at {blockedTimeSlot}",
                            ImmutableArray.Create(assignment.SessionId),
                            $"lecturer={lecturer.Name} scheduled_at={assignment.TimeSlot} blocked_at={blockedTimeSlot}");
                    }
                }
            }
        }
    }

    private static IEnumerable<ConstraintViolation> CheckLhpInternal(
        Schedule schedule,
        IReadOnlyDictionary<string, Session> sessions,
        IReadOnlyDictionary<string, Assignment> assignments,
        SchedulingProblem problem,
        CancellationToken cancellationToken)
    {
        var assigned = AssignedSessionsWithFixedOnline(schedule, sessions, assignments, problem).ToArray();

        foreach (var (left, right) in OverlappingPairs(
                     assigned,
                     item => item.Session.LhpCode,
                     item => item.Assignment.TimeSlot,
                     item => item.Assignment.SessionId,
                     cancellationToken))
        {
            var sharedCohorts = left.Session.StudentCohorts
                .Select(cohort => cohort.Code)
                .Intersect(right.Session.StudentCohorts.Select(cohort => cohort.Code), StringComparer.Ordinal)
                .ToImmutableArray();
            if (!sharedCohorts.IsEmpty)
            {
                var cohortLabel = $"{{{string.Join(", ", sharedCohorts)}}}";
                yield return new ConstraintViolation(
                    "HC-5",
                    "lhp_internal_no_overlap",
                    $"LHP '{left.Session.LhpCode}' has overlapping sessions: {left.Assignment.SessionId} ({left.Session.SessionType.ToWorkbookValue()}) and {right.Assignment.SessionId} ({right.Session.SessionType.ToWorkbookValue()}) at {left.Assignment.TimeSlot}. Shared cohorts: {cohortLabel}",
                    ImmutableArray.Create(left.Assignment.SessionId, right.Assignment.SessionId),
                    $"lhp={left.Session.LhpCode} cohorts={cohortLabel} timeslot={left.Assignment.TimeSlot}");
            }
        }
    }

    private static IEnumerable<ConstraintViolation> CheckRoomCompatibility(
        Schedule schedule,
        Dictionary<string, Session> sessions,
        CancellationToken cancellationToken)
    {
        foreach (var assignment in schedule.Assignments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sessions.TryGetValue(assignment.SessionId, out var session) &&
                session.SessionType.IsLab() &&
                !assignment.Room.IsLab)
            {
                yield return new ConstraintViolation(
                    "HC-6",
                    "room_compatibility",
                    $"Session {assignment.SessionId} ({session.SessionType.ToWorkbookValue()}) requires a lab room but was assigned to {assignment.Room.Code}",
                    ImmutableArray.Create(assignment.SessionId),
                    $"session={assignment.SessionId} type={session.SessionType.ToWorkbookValue()} room={assignment.Room.Code}");
            }
        }
    }

    private static IEnumerable<ConstraintViolation> CheckPersonalChoiceUniqueness(
        ImmutableArray<PersonalSelectionChoice> choices,
        CancellationToken cancellationToken)
    {
        var sessionPairs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var choice in choices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pairLabel = $"{choice.DesiredAssignment.CourseCode}::{TeamIdentity(choice.DesiredAssignment)}";
            foreach (var sessionId in choice.SessionIds)
            {
                if (!sessionPairs.TryGetValue(sessionId, out var pairs))
                {
                    pairs = [];
                    sessionPairs.Add(sessionId, pairs);
                }

                pairs.Add(pairLabel);
            }
        }

        foreach (var (sessionId, pairs) in sessionPairs.Where(item => item.Value.Count > 1))
        {
            yield return new ConstraintViolation(
                "PT-5",
                "selected_session_reused",
                $"Session '{sessionId}' is reused across multiple selected teaching-team requests.",
                ImmutableArray.Create(sessionId),
                $"session={sessionId} pairs=({string.Join(", ", pairs.OrderBy(pair => pair, StringComparer.Ordinal))})");
        }
    }

    private static IEnumerable<ConstraintViolation> CheckPersonalChoiceOverlap(
        ImmutableArray<PersonalSelectionChoice> choices,
        IReadOnlyDictionary<string, Assignment> assignments,
        IReadOnlyDictionary<string, Session> sessions,
        IReadOnlySet<string> onlineSessionIds,
        CancellationToken cancellationToken)
    {
        foreach (var (leftChoice, rightChoice) in Pairs(choices))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftTimeslots = ResolveChoiceTimeSlots(leftChoice.SessionIds, assignments, sessions, onlineSessionIds);
            var rightTimeslots = ResolveChoiceTimeSlots(rightChoice.SessionIds, assignments, sessions, onlineSessionIds);
            foreach (var (leftSessionId, leftTimeSlot) in leftTimeslots)
            {
                foreach (var (rightSessionId, rightTimeSlot) in rightTimeslots)
                {
                    if (leftTimeSlot.OverlapsWith(rightTimeSlot))
                    {
                        yield return new ConstraintViolation(
                            "PT-6",
                            "selected_lhp_no_overlap",
                            $"Chosen LHPs '{leftChoice.LhpCode}' and '{rightChoice.LhpCode}' overlap at {leftTimeSlot}.",
                            ImmutableArray.Create(leftSessionId, rightSessionId),
                            $"left={leftChoice.LhpCode} right={rightChoice.LhpCode} timeslot={leftTimeSlot}");
                    }
                }
            }
        }
    }

    private static IEnumerable<(Assignment Assignment, Session Session)> AssignedSessionsWithFixedOnline(
        Schedule schedule,
        IReadOnlyDictionary<string, Session> sessions,
        IReadOnlyDictionary<string, Assignment> assignments,
        SchedulingProblem problem)
    {
        foreach (var assignment in schedule.Assignments)
        {
            if (sessions.TryGetValue(assignment.SessionId, out var session))
            {
                yield return (assignment, session);
            }
        }

        foreach (var session in problem.OnlineSessions)
        {
            if (session.TimeSlot is not null && session.Room is not null && !assignments.ContainsKey(session.SessionId))
            {
                yield return (new Assignment(session.SessionId, session.TimeSlot, session.Room), session);
            }
        }
    }

    private static Dictionary<string, TimeSlot> ResolveChoiceTimeSlots(
        ImmutableArray<string> sessionIds,
        IReadOnlyDictionary<string, Assignment> assignments,
        IReadOnlyDictionary<string, Session> sessions,
        IReadOnlySet<string> onlineSessionIds)
    {
        var resolved = new Dictionary<string, TimeSlot>(StringComparer.Ordinal);
        foreach (var sessionId in sessionIds)
        {
            if (assignments.TryGetValue(sessionId, out var assignment))
            {
                resolved[sessionId] = assignment.TimeSlot;
            }
            else if (onlineSessionIds.Contains(sessionId) &&
                     sessions.TryGetValue(sessionId, out var session) &&
                     session.TimeSlot is not null)
            {
                resolved[sessionId] = session.TimeSlot;
            }
        }

        return resolved;
    }

    private static bool TimeSlotMapsEqual(
        Dictionary<string, TimeSlot> actual,
        ImmutableArray<KeyValuePair<string, TimeSlot>> expected) =>
        actual.Count == expected.Length && expected.All(pair => actual.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static IEnumerable<(T Left, T Right)> OverlappingPairs<T>(
        IEnumerable<T> values,
        Func<T, string> resourceSelector,
        Func<T, TimeSlot> timeSlotSelector,
        Func<T, string> idSelector,
        CancellationToken cancellationToken)
    {
        var buckets = new Dictionary<(string Resource, Day Day, int Period), List<T>>();
        foreach (var item in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timeSlot = timeSlotSelector(item);
            foreach (var period in timeSlot.Period.ToAtomicPeriods())
            {
                var key = (resourceSelector(item), timeSlot.Day, period);
                if (!buckets.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buckets.Add(key, bucket);
                }

                bucket.Add(item);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, bucket) in buckets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var index = 0; index < bucket.Count; index++)
            {
                for (var next = index + 1; next < bucket.Count; next++)
                {
                    var left = bucket[index];
                    var right = bucket[next];
                    var leftId = idSelector(left);
                    var rightId = idSelector(right);
                    if (string.Equals(leftId, rightId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var leftComesFirst = string.CompareOrdinal(leftId, rightId) <= 0;
                    var first = leftComesFirst ? leftId : rightId;
                    var second = leftComesFirst ? rightId : leftId;
                    var pairKey = $"{key.Resource}\u001f{first}\u001f{second}";
                    if (seen.Add(pairKey))
                    {
                        yield return (left, right);
                    }
                }
            }
        }
    }

    private static IEnumerable<(T Left, T Right)> Pairs<T>(IEnumerable<T> values)
    {
        var items = values.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            for (var next = index + 1; next < items.Length; next++)
            {
                yield return (items[index], items[next]);
            }
        }
    }
}
