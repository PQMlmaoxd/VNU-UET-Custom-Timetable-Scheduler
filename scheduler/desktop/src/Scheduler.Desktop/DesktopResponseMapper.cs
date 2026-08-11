using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure.Timetable;
using Scheduler.Infrastructure.Xlsx;

namespace Scheduler.Desktop;

/// <summary>
/// Preserves the current React response contract while replacing the local HTTP API.
/// </summary>
public static class DesktopResponseMapper
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static JsonElement ToJson<T>(T value) => JsonSerializer.SerializeToElement(value, SerializerOptions);

    public static ValidateExistingResponse CreateValidateResponse(
        string workbookPath,
        TimetableParseResult parseResult,
        ValidationResult validation)
    {
        var anchors = CreateAnchors(parseResult.Problem);
        return new ValidateExistingResponse(
            "validate_existing",
            workbookPath,
            parseResult.Problem.Department,
            CreateParseSummary(parseResult, anchors, 0),
            new PrototypeCatalog(anchors, CreateRoomCostRules()),
            CreateValidationSummary(validation));
    }

    public static RescheduleResponse CreateSolveResponse(
        string workbookPath,
        TimetableParseResult parseResult,
        ImmutableArray<DesiredAnchorAssignment> desiredAssignments,
        ValidationResult existingValidation,
        PersonalSelectionSolveResult solveResult,
        string? formalVerificationToken = null)
    {
        var anchors = CreateAnchors(parseResult.Problem);
        var solutions = solveResult.Solutions
            .Select((solution, index) => new SelectedSolutionItem(
                index + 1,
                solution.MovementCost,
                CreateDesiredSummaries(parseResult.Problem, solution.Choices)))
            .ToImmutableArray();
        var solver = CreateSolverSummary(solveResult, desiredAssignments.Length, formalVerificationToken);

        return new RescheduleResponse(
            "reschedule",
            workbookPath,
            parseResult.Problem.Department,
            CreateParseSummary(parseResult, anchors, desiredAssignments.Length),
            new PrototypeCatalog(anchors, CreateRoomCostRules()),
            solutions.IsEmpty ? [] : solutions[0].DesiredAssignments,
            solutions,
            CreateValidationSummary(existingValidation),
            solver,
            null);
    }

    private static ImmutableArray<AnchorCatalogItem> CreateAnchors(SchedulingProblem problem) =>
        TeachingUnitCatalog.Create(problem).Units
            .GroupBy(unit => (unit.CourseCode, unit.TeachingTeamKey, unit.TeachingTeamLabel))
            .OrderBy(group => group.Key.CourseCode, StringComparer.Ordinal)
            .ThenBy(group => group.Key.TeachingTeamLabel, StringComparer.Ordinal)
            .Select(group => new AnchorCatalogItem(
                group.Key.CourseCode,
                group.First().Sessions[0].Course.Name,
                group.Key.TeachingTeamKey,
                group.Key.TeachingTeamLabel,
                group.Sum(unit => unit.Sessions.Length)))
            .ToImmutableArray();

    private static ParseSummary CreateParseSummary(
        TimetableParseResult parseResult,
        ImmutableArray<AnchorCatalogItem> anchors,
        int requestedAssignments) => new(
        parseResult.Problem.Sessions.Length,
        parseResult.Problem.SchedulableSessions.Length,
        parseResult.Problem.OnlineSessions.Length,
        parseResult.OtherDepartmentSessions.Length,
        parseResult.Problem.LecturerBlocks.Length,
        anchors.Length,
        requestedAssignments,
        parseResult.Problem.AvailableRooms.Length,
        parseResult.SkippedRows.Length,
        parseResult.FatalWarnings.Length,
        parseResult.Warnings.Take(10).ToImmutableArray());

    private static ImmutableArray<DesiredAnchorSummary> CreateDesiredSummaries(
        SchedulingProblem problem,
        ImmutableArray<PersonalSelectionChoice> choices)
    {
        var sessionsById = problem.Sessions.ToDictionary(session => session.SessionId, StringComparer.Ordinal);
        return choices
            .Select(choice => CreateDesiredSummary(choice, sessionsById))
            .ToImmutableArray();
    }

    private static DesiredAnchorSummary CreateDesiredSummary(
        PersonalSelectionChoice choice,
        Dictionary<string, Session> sessionsById)
    {
        var sessions = choice.SessionIds
            .Where(sessionsById.ContainsKey)
            .Select(sessionId => sessionsById[sessionId])
            .ToImmutableArray();
        var lhpSchedules = CreateLhpSchedules(sessions);
        var courseName = string.IsNullOrEmpty(choice.DesiredAssignment.CourseName)
            ? sessions.FirstOrDefault()?.Course.Name ?? string.Empty
            : choice.DesiredAssignment.CourseName;

        return new DesiredAnchorSummary(
            choice.DesiredAssignment.CourseCode,
            choice.DesiredAssignment.TeachingTeamKey,
            choice.DesiredAssignment.DisplayTeachingTeam,
            courseName,
            [choice.LhpCode],
            sessions.Length,
            lhpSchedules.SelectMany(schedule => schedule.MatchedSessions).ToImmutableArray(),
            lhpSchedules);
    }

    private static ImmutableArray<LhpScheduleItem> CreateLhpSchedules(ImmutableArray<Session> sessions) =>
        sessions
            .GroupBy(session => session.LhpCode, StringComparer.Ordinal)
            .Select(group => new LhpScheduleItem(
                group.Key,
                group
                    .OrderBy(SessionSortKey)
                    .Select(CreateSessionScheduleItem)
                    .ToImmutableArray()))
            .OrderBy(schedule => SessionSortKey(sessions.First(session => session.LhpCode == schedule.LhpCode)))
            .ThenBy(schedule => schedule.LhpCode, StringComparer.Ordinal)
            .ToImmutableArray();

    private static SessionScheduleItem CreateSessionScheduleItem(Session session)
    {
        var timeSlot = session.TimeSlot;
        var isOnline = session.SessionType == SessionType.Onl;
        var timeSlotLabel = isOnline
            ? timeSlot is null ? "Online" : $"{timeSlot} (Online)"
            : timeSlot?.ToString() ?? "Unassigned";

        return new SessionScheduleItem(
            session.SessionId,
            session.Course.Code,
            session.Course.Name,
            session.LhpCode,
            session.SessionType.ToWorkbookValue(),
            session.Lecturers.Select(lecturer => lecturer.Name).ToImmutableArray(),
            session.StudentCohorts.Select(cohort => cohort.Code).ToImmutableArray(),
            timeSlotLabel,
            isOnline ? "ONL" : session.Room?.Code ?? "-",
            session.SourceRow,
            timeSlot is null ? null : (int)timeSlot.Day,
            timeSlot?.Period.ToWorkbookValue(),
            timeSlot?.Period.ToAtomicPeriods().Order()
                .Select(period => period.ToString(CultureInfo.InvariantCulture))
                .ToImmutableArray());
    }

    private static (int Slot, string CourseCode, string LhpCode, int SourceRow) SessionSortKey(Session session)
    {
        if (session.TimeSlot is null)
        {
            return (99, session.Course.Code, session.LhpCode, session.SourceRow);
        }

        return (
            ((int)session.TimeSlot.Day * 10) + session.TimeSlot.Period.ToAtomicPeriods().Min(),
            session.Course.Code,
            session.LhpCode,
            session.SourceRow);
    }

    private static SolverSummary CreateSolverSummary(
        PersonalSelectionSolveResult solveResult,
        int assignmentCount,
        string? formalVerificationToken)
    {
        var (status, satisfiability, explanation) = solveResult.SolverResult.Status switch
        {
            PersonalSelectionSatStatus.Feasible => (
                "feasible",
                "SAT",
                ImmutableArray.Create($"Đã tìm thấy {solveResult.Solutions.Length} phương án.")),
            PersonalSelectionSatStatus.Infeasible => (
                "infeasible",
                "UNSAT",
                ImmutableArray.Create("Không có phương án phù hợp với các môn đã chọn.")),
            PersonalSelectionSatStatus.TimedOut => (
                "timeout",
                "UNKNOWN",
                ImmutableArray.Create("Tìm lịch đã hết thời gian cho phép.")),
            _ => throw new ArgumentOutOfRangeException(nameof(solveResult), "Unsupported SAT status."),
        };

        return new SolverSummary(
            "cadical",
            status,
            satisfiability,
            checked((int)Math.Ceiling(solveResult.SolverResult.Elapsed.TotalMilliseconds)),
            null,
            assignmentCount,
            solveResult.Solutions.Length,
            "Bộ tìm lịch CaDiCaL",
            explanation,
            formalVerificationToken);
    }

    private static ImmutableArray<RoomCostRuleItem> CreateRoomCostRules() =>
    [
        RoomCostRule("same room", "same room", "101-A", "101-A", "Cùng phòng: không cần di chuyển"),
        RoomCostRule("A", "A", "101-A", "102-A", "Khác phòng trong cùng tòa nhà"),
        RoomCostRule("A", "B", "101-A", "101-B", "Khác tòa nhà trong cùng khu GD4"),
        RoomCostRule("G2", "E5", "202-G2", "404-E5", "Khác tòa nhà trong cùng khu GD2"),
        RoomCostRule("A", "T", "101-A", "101-T", "Khác khuôn viên"),
        RoomCostRule("T", "ĐHKHTN", "101-T", "803-T5 ĐHKHTN", "Di chuyển giữa khu học tập và ĐHKHTN"),
    ];

    private static RoomCostRuleItem RoomCostRule(
        string fromZone,
        string toZone,
        string fromRoomCode,
        string toRoomCode,
        string description) => new(
        fromZone,
        toZone,
        new Room(fromRoomCode).TransitionCostTo(new Room(toRoomCode)),
        description);

    private static ValidationSummary CreateValidationSummary(ValidationResult validation) => new(
        validation.IsValid,
        validation.IsComplete,
        validation.ViolationCount,
        validation.MissingSessionIds.Length,
        validation.HardViolations.Take(10).Select(violation => violation.ToString()).ToImmutableArray());

    public sealed record ValidateExistingResponse(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("workbook_path")] string WorkbookPath,
        [property: JsonPropertyName("department")] string Department,
        [property: JsonPropertyName("parse_summary")] ParseSummary ParseSummary,
        [property: JsonPropertyName("prototype_catalog")] PrototypeCatalog PrototypeCatalog,
        [property: JsonPropertyName("existing_schedule_validation")] ValidationSummary ExistingScheduleValidation);

    public sealed record RescheduleResponse(
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("workbook_path")] string WorkbookPath,
        [property: JsonPropertyName("department")] string Department,
        [property: JsonPropertyName("parse_summary")] ParseSummary ParseSummary,
        [property: JsonPropertyName("prototype_catalog")] PrototypeCatalog PrototypeCatalog,
        [property: JsonPropertyName("desired_assignments")] ImmutableArray<DesiredAnchorSummary> DesiredAssignments,
        [property: JsonPropertyName("solutions")] ImmutableArray<SelectedSolutionItem> Solutions,
        [property: JsonPropertyName("existing_schedule_validation")] ValidationSummary ExistingScheduleValidation,
        [property: JsonPropertyName("solver")] SolverSummary Solver,
        [property: JsonPropertyName("solved_schedule_validation")] ValidationSummary? SolvedScheduleValidation);

    public sealed record ParseSummary(
        [property: JsonPropertyName("sessions")] int Sessions,
        [property: JsonPropertyName("schedulable_sessions")] int SchedulableSessions,
        [property: JsonPropertyName("online_sessions")] int OnlineSessions,
        [property: JsonPropertyName("other_department_sessions")] int OtherDepartmentSessions,
        [property: JsonPropertyName("lecturer_blocks")] int LecturerBlocks,
        [property: JsonPropertyName("anchor_count")] int AnchorCount,
        [property: JsonPropertyName("requested_assignments")] int RequestedAssignments,
        [property: JsonPropertyName("rooms")] int Rooms,
        [property: JsonPropertyName("skipped_rows")] int SkippedRows,
        [property: JsonPropertyName("fatal_warning_count")] int FatalWarningCount,
        [property: JsonPropertyName("warnings")] ImmutableArray<string> Warnings);

    public sealed record PrototypeCatalog(
        [property: JsonPropertyName("anchors")] ImmutableArray<AnchorCatalogItem> Anchors,
        [property: JsonPropertyName("room_cost_rules")] ImmutableArray<RoomCostRuleItem> RoomCostRules);

    public sealed record AnchorCatalogItem(
        [property: JsonPropertyName("course_code")] string CourseCode,
        [property: JsonPropertyName("course_name")] string CourseName,
        [property: JsonPropertyName("teaching_team_key")] string TeachingTeamKey,
        [property: JsonPropertyName("teaching_team_label")] string TeachingTeamLabel,
        [property: JsonPropertyName("session_count")] int SessionCount);

    public sealed record RoomCostRuleItem(
        [property: JsonPropertyName("from_zone")] string FromZone,
        [property: JsonPropertyName("to_zone")] string ToZone,
        [property: JsonPropertyName("cost")] int Cost,
        [property: JsonPropertyName("description")] string Description);

    public sealed record ValidationSummary(
        [property: JsonPropertyName("is_valid")] bool IsValid,
        [property: JsonPropertyName("is_complete")] bool IsComplete,
        [property: JsonPropertyName("violation_count")] int ViolationCount,
        [property: JsonPropertyName("missing_session_count")] int MissingSessionCount,
        [property: JsonPropertyName("sample_violations")] ImmutableArray<string> SampleViolations);

    public sealed record DesiredAnchorSummary(
        [property: JsonPropertyName("course_code")] string CourseCode,
        [property: JsonPropertyName("teaching_team_key")] string TeachingTeamKey,
        [property: JsonPropertyName("teaching_team_label")] string TeachingTeamLabel,
        [property: JsonPropertyName("course_name")] string CourseName,
        [property: JsonPropertyName("lhp_codes")] ImmutableArray<string> LhpCodes,
        [property: JsonPropertyName("session_count")] int SessionCount,
        [property: JsonPropertyName("matched_sessions")] ImmutableArray<SessionScheduleItem> MatchedSessions,
        [property: JsonPropertyName("lhp_schedules")] ImmutableArray<LhpScheduleItem> LhpSchedules);

    public sealed record LhpScheduleItem(
        [property: JsonPropertyName("lhp_code")] string LhpCode,
        [property: JsonPropertyName("matched_sessions")] ImmutableArray<SessionScheduleItem> MatchedSessions)
    {
        [JsonPropertyName("session_count")]
        public int SessionCount => MatchedSessions.Length;
    }

    public sealed record SessionScheduleItem(
        [property: JsonPropertyName("session_id")] string SessionId,
        [property: JsonPropertyName("course_code")] string CourseCode,
        [property: JsonPropertyName("course_name")] string CourseName,
        [property: JsonPropertyName("lhp_code")] string LhpCode,
        [property: JsonPropertyName("session_type")] string SessionType,
        [property: JsonPropertyName("lecturer_names")] ImmutableArray<string> LecturerNames,
        [property: JsonPropertyName("cohort_codes")] ImmutableArray<string> CohortCodes,
        [property: JsonPropertyName("timeslot_label")] string TimeslotLabel,
        [property: JsonPropertyName("room_code")] string RoomCode,
        [property: JsonPropertyName("source_row")] int SourceRow,
        [property: JsonPropertyName("day")] int? Day,
        [property: JsonPropertyName("period_code")] string? PeriodCode,
        [property: JsonPropertyName("period_atomic")] ImmutableArray<string>? PeriodAtomic);

    public sealed record SelectedSolutionItem(
        [property: JsonPropertyName("solution_index")] int SolutionIndex,
        [property: JsonPropertyName("movement_cost")] int MovementCost,
        [property: JsonPropertyName("desired_assignments")] ImmutableArray<DesiredAnchorSummary> DesiredAssignments);

    public sealed record SolverSummary(
        [property: JsonPropertyName("backend")] string Backend,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("satisfiability")] string Satisfiability,
        [property: JsonPropertyName("solve_time_ms")] int SolveTimeMilliseconds,
        [property: JsonPropertyName("objective_value")] double? ObjectiveValue,
        [property: JsonPropertyName("assignment_count")] int AssignmentCount,
        [property: JsonPropertyName("solution_count")] int SolutionCount,
        [property: JsonPropertyName("solver_info")] string SolverInfo,
        [property: JsonPropertyName("explanation")] ImmutableArray<string> Explanation,
        [property: JsonPropertyName("formal_verification_token")] string? FormalVerificationToken);
}
