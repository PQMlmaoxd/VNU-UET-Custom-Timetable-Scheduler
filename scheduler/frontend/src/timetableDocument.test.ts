import { describe, expect, it } from "vitest";

import {
  parseTimetableDocument,
  TIMETABLE_DOCUMENT_SCHEMA,
  TIMETABLE_DOCUMENT_VERSION,
} from "./timetableDocument";

const session = {
  session_id: "row_1",
  course_code: "INT2213",
  course_name: "Mạng Máy Tính",
  lhp_code: "INT2213-01",
  session_type: "LT",
  lecturer_names: ["Nguyễn A"],
  cohort_codes: ["K69I"],
  timeslot_label: "Thứ 2 Ca 1",
  room_code: "105-B",
  source_row: 1,
  day: 2,
  period_code: "1",
  period_atomic: ["1"],
};

const desiredAssignment = {
  course_code: "INT2213",
  teaching_team_key: "team-a",
  teaching_team_label: "Nguyễn A",
  course_name: "Mạng Máy Tính",
  lhp_codes: ["INT2213-01"],
  session_count: 1,
  matched_sessions: [session],
  lhp_schedules: [{
    lhp_code: "INT2213-01",
    session_count: 1,
    matched_sessions: [session],
  }],
};

const timetable = {
  mode: "reschedule",
  workbook_path: "timetable.xlsx",
  department: "CNTT",
  parse_summary: {
    sessions: 1,
    schedulable_sessions: 1,
    online_sessions: 0,
    other_department_sessions: 0,
    lecturer_blocks: 0,
    anchor_count: 1,
    requested_assignments: 1,
    rooms: 1,
    skipped_rows: 0,
    fatal_warning_count: 0,
    partial_import: false,
    quarantined_lhp_count: 0,
    quarantined_session_count: 0,
    warnings: [],
    fatal_warnings: [],
    quarantined_offerings: [],
  },
  prototype_catalog: {
    anchors: [{
      course_code: "INT2213",
      course_name: "Mạng Máy Tính",
      teaching_team_key: "team-a",
      teaching_team_label: "Nguyễn A",
      session_count: 1,
    }],
    room_cost_rules: [],
  },
  desired_assignments: [desiredAssignment],
  existing_schedule_validation: {
    is_valid: true,
    is_complete: true,
    violation_count: 0,
    missing_session_count: 0,
    sample_violations: [],
  },
  solved_schedule_validation: null,
  solver: {
    backend: "personal_sat",
    status: "feasible",
    satisfiability: "SAT",
    solve_time_ms: 12,
    objective_value: null,
    assignment_count: 1,
    solution_count: 1,
    solver_info: "fixture",
    explanation: [],
    formal_verification_token: null,
  },
  solutions: [{
    solution_index: 1,
    movement_cost: 0,
    desired_assignments: [desiredAssignment],
  }],
};

function documentWith(overrides: Record<string, unknown> = {}) {
  return {
    schema: TIMETABLE_DOCUMENT_SCHEMA,
    schema_version: TIMETABLE_DOCUMENT_VERSION,
    saved_at_utc: "2026-08-21T08:30:00Z",
    app_version: "1.0.1",
    source: { file_name: "timetable.xlsx" },
    timetable,
    ...overrides,
  };
}

describe("saved timetable documents", () => {
  it("parses a valid SAT document", () => {
    const result = parseTimetableDocument(JSON.stringify(documentWith()));

    expect(result).toEqual({ ok: true, document: documentWith() });
  });

  it("accepts a UTF-8 BOM", () => {
    const result = parseTimetableDocument(`\uFEFF${JSON.stringify(documentWith())}`);

    expect(result.ok).toBe(true);
  });

  it.each([
    ["", "invalid-json"],
    ["not json", "invalid-json"],
    ["null", "invalid-envelope"],
    ["[]", "invalid-envelope"],
  ] as const)("rejects malformed JSON input %j", (text, code) => {
    expect(parseTimetableDocument(text)).toEqual({ ok: false, code });
  });

  it("rejects a document with a different schema", () => {
    expect(parseTimetableDocument(JSON.stringify(documentWith({ schema: "other" })))).toEqual({
      ok: false,
      code: "invalid-envelope",
    });
  });

  it.each([
    [0, "unsupported-version"],
    [2, "newer-version"],
    ["1", "invalid-envelope"],
  ] as const)("rejects schema version %j", (version, code) => {
    expect(parseTimetableDocument(JSON.stringify(documentWith({ schema_version: version })))).toEqual({
      ok: false,
      code,
    });
  });

  it("rejects an invalid timetable payload", () => {
    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: { ...timetable, solutions: [{}] } })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects a non-SAT timetable", () => {
    const unsatTimetable = {
      ...timetable,
      desired_assignments: [],
      solver: { ...timetable.solver, satisfiability: "UNSAT", status: "infeasible", solution_count: 0 },
      solutions: [],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: unsatTimetable })))).toEqual({
      ok: false,
      code: "not-sat",
    });
  });

  it("rejects malformed nested solver metadata", () => {
    const invalidTimetable = {
      ...timetable,
      solver: { ...timetable.solver, solution_count: 2 },
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects malformed saved-document metadata strings", () => {
    const invalidDocuments = [
      documentWith({ saved_at_utc: "\uD800" }),
      documentWith({ app_version: "\uFFFD" }),
      documentWith({ source: { file_name: "\uDC00" } }),
    ];

    for (const invalidDocument of invalidDocuments) {
      expect(parseTimetableDocument(JSON.stringify(invalidDocument))).toEqual({
        ok: false,
        code: "invalid-envelope",
      });
    }
  });

  it("rejects malformed response metadata and string arrays", () => {
    const malformedTimetables = [
      {
        ...timetable,
        parse_summary: { ...timetable.parse_summary, warnings: ["\uFFFD"] },
      },
      {
        ...timetable,
        parse_summary: {
          ...timetable.parse_summary,
          quarantined_offerings: [{
            course_code: "\uD800",
            lhp_code: "LHP-1",
            reason_code: "invalid",
            source_locations: [],
            quarantined_row_count: 1,
            excluded_session_count: 1,
          }],
        },
      },
      {
        ...timetable,
        prototype_catalog: {
          ...timetable.prototype_catalog,
          anchors: [{ ...timetable.prototype_catalog.anchors[0], course_name: "\uFFFD" }],
        },
      },
      {
        ...timetable,
        existing_schedule_validation: {
          ...timetable.existing_schedule_validation,
          sample_violations: ["\uDC00"],
        },
      },
      {
        ...timetable,
        solver: { ...timetable.solver, solver_info: "\uFFFD" },
      },
      {
        ...timetable,
        solver: { ...timetable.solver, backend: "\uD800" },
      },
      {
        ...timetable,
        solver: { ...timetable.solver, explanation: ["\uD800"] },
      },
      {
        ...timetable,
        desired_assignments: [{
          ...desiredAssignment,
          matched_sessions: [{ ...session, lecturer_names: ["\uFFFD"] }],
          lhp_schedules: [{
            ...desiredAssignment.lhp_schedules[0],
            matched_sessions: [{ ...session, lecturer_names: ["\uFFFD"] }],
          }],
        }],
      },
    ];

    for (const invalidTimetable of malformedTimetables) {
      expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
        ok: false,
        code: "invalid-timetable",
      });
    }
  });

  it("rejects more solutions than the bounded solver contract", () => {
    const tooManySolutions = Array.from({ length: 6 }, (_, index) => ({
      ...timetable.solutions[0],
      solution_index: index + 1,
    }));
    const invalidTimetable = {
      ...timetable,
      solver: { ...timetable.solver, solution_count: tooManySolutions.length },
      solutions: tooManySolutions,
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects a SAT timetable without renderable assignments", () => {
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects unknown response properties before native re-save", () => {
    const invalidTimetable = {
      ...timetable,
      solver: { ...timetable.solver, extra: true },
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects integer values outside the native response range", () => {
    const invalidTimetable = {
      ...timetable,
      parse_summary: { ...timetable.parse_summary, sessions: 2147483648 },
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects LHP schedules whose session count does not match their sessions", () => {
    const invalidDesiredAssignment = {
      ...desiredAssignment,
      lhp_schedules: desiredAssignment.lhp_schedules.map((schedule) => ({
        ...schedule,
        session_count: 0,
      })),
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects session fields outside the renderable timetable domain", () => {
    const invalidSessions = [
      { ...session, session_type: "INVALID" },
      { ...session, day: 99 },
      { ...session, period_code: "5" },
      { ...session, period_atomic: ["5"] },
    ];

    for (const invalidSession of invalidSessions) {
      const invalidDesiredAssignment = {
        ...desiredAssignment,
        matched_sessions: [invalidSession],
        lhp_schedules: [{
          ...desiredAssignment.lhp_schedules[0],
          matched_sessions: [invalidSession],
        }],
      };
      const invalidTimetable = {
        ...timetable,
        desired_assignments: [invalidDesiredAssignment],
        solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
      };

      expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
        ok: false,
        code: "invalid-timetable",
      });
    }
  });

  it("rejects atomic periods that do not match the period code", () => {
    const invalidSession = { ...session, period_code: "1-2", period_atomic: ["1", "3"] };
    const invalidDesiredAssignment = {
      ...desiredAssignment,
      matched_sessions: [invalidSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        matched_sessions: [invalidSession],
      }],
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects negative movement costs", () => {
    const invalidTimetable = {
      ...timetable,
      solutions: [{ ...timetable.solutions[0], movement_cost: -1 }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects movement costs that do not match the saved sessions", () => {
    const invalidTimetable = {
      ...timetable,
      solutions: [{ ...timetable.solutions[0], movement_cost: 1 }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects movement costs that are not ordered by recomputed cost", () => {
    const secondSession = {
      ...session,
      session_id: "row_2",
      source_row: 2,
      timeslot_label: "Thứ 2 Ca 2",
      room_code: "999-Z",
      period_code: "2",
      period_atomic: ["2"],
    };
    const firstAssignment = {
      ...desiredAssignment,
      session_count: 2,
      matched_sessions: [session, secondSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        session_count: 2,
        matched_sessions: [session, secondSession],
      }],
    };
    const alternateSession = { ...secondSession, room_code: "105-B" };
    const secondAssignment = {
      ...firstAssignment,
      matched_sessions: [session, alternateSession],
      lhp_schedules: [{
        ...firstAssignment.lhp_schedules[0],
        matched_sessions: [session, alternateSession],
      }],
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [firstAssignment],
      solver: { ...timetable.solver, solution_count: 2 },
      solutions: [
        { solution_index: 1, movement_cost: 3, desired_assignments: [firstAssignment] },
        { solution_index: 2, movement_cost: 0, desired_assignments: [secondAssignment] },
      ],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("uses ordinal session IDs when recomputing movement cost", () => {
    const firstSamePeriodSession = {
      ...session,
      session_id: "sheet_1_row_5",
      source_row: 5,
      room_code: "105-A",
    };
    const secondSamePeriodSession = {
      ...session,
      session_id: "sheet_10_row_5",
      source_row: 5,
      room_code: "201-G2",
    };
    const followingSession = {
      ...session,
      session_id: "sheet_2_row_6",
      source_row: 6,
      timeslot_label: "Thứ 2 Ca 2",
      room_code: "202-G2",
      period_code: "2",
      period_atomic: ["2"],
    };
    const sessions = [firstSamePeriodSession, secondSamePeriodSession, followingSession];
    const ordinalAssignment = {
      ...desiredAssignment,
      session_count: sessions.length,
      matched_sessions: sessions,
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        session_count: sessions.length,
        matched_sessions: sessions,
      }],
    };
    const validTimetable = {
      ...timetable,
      desired_assignments: [ordinalAssignment],
      solutions: [{
        ...timetable.solutions[0],
        movement_cost: 3,
        desired_assignments: [ordinalAssignment],
      }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: validTimetable }))).ok).toBe(true);
  });

  it("accepts a valid alternate solution placement with the same requested anchor", () => {
    const alternateSession = { ...session, room_code: "106-B" };
    const alternateAssignment = {
      ...desiredAssignment,
      matched_sessions: [alternateSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        matched_sessions: [alternateSession],
      }],
    };
    const alternateSolution = {
      ...timetable.solutions[0],
      solution_index: 2,
      desired_assignments: [alternateAssignment],
    };
    const validTimetable = {
      ...timetable,
      solver: { ...timetable.solver, solution_count: 2 },
      solutions: [timetable.solutions[0], alternateSolution],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: validTimetable }))).ok).toBe(true);
  });

  it("requires every solution to preserve the requested desired assignments", () => {
    const secondSolution = {
      ...timetable.solutions[0],
      solution_index: 2,
      desired_assignments: [{ ...desiredAssignment, course_name: "Khác" }],
    };
    const invalidTimetable = {
      ...timetable,
      solver: { ...timetable.solver, solution_count: 2 },
      solutions: [timetable.solutions[0], secondSolution],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it.each([
    ["physical sessions without timing", { day: null, period_code: null, period_atomic: null }],
    ["physical sessions with a blank room", { room_code: " " }],
  ] as const)("rejects %s", (_name, changes) => {
    const invalidSession = { ...session, ...changes };
    const invalidDesiredAssignment = {
      ...desiredAssignment,
      matched_sessions: [invalidSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        matched_sessions: [invalidSession],
      }],
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("accepts an online session without a timing", () => {
    const onlineSession = {
      ...session,
      session_type: "ONL",
      room_code: "ONL",
      day: null,
      period_code: null,
      period_atomic: null,
    };
    const onlineAssignment = {
      ...desiredAssignment,
      matched_sessions: [onlineSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        matched_sessions: [onlineSession],
      }],
    };
    const onlineTimetable = {
      ...timetable,
      desired_assignments: [onlineAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [onlineAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: onlineTimetable }))).ok).toBe(true);
  });

  it("rejects duplicate desired anchor identities", () => {
    const duplicateSession = { ...session, session_id: "row_2", source_row: 2 };
    const duplicateAssignment = {
      ...desiredAssignment,
      matched_sessions: [duplicateSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        matched_sessions: [duplicateSession],
      }],
    };
    const invalidAssignments = [desiredAssignment, duplicateAssignment];
    const invalidTimetable = {
      ...timetable,
      desired_assignments: invalidAssignments,
      solutions: [{ ...timetable.solutions[0], desired_assignments: invalidAssignments }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects duplicate solution placements", () => {
    const duplicateAssignment = {
      ...desiredAssignment,
      matched_sessions: [{ ...session, source_row: 99, timeslot_label: "Metadata only" }],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        matched_sessions: [{ ...session, source_row: 99, timeslot_label: "Metadata only" }],
      }],
    };
    const duplicateSolution = {
      ...timetable.solutions[0],
      solution_index: 2,
      desired_assignments: [duplicateAssignment],
    };
    const invalidTimetable = {
      ...timetable,
      solver: { ...timetable.solver, solution_count: 2 },
      solutions: [timetable.solutions[0], duplicateSolution],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects desired assignments whose session count disagrees with rendered sessions", () => {
    const invalidDesiredAssignment = { ...desiredAssignment, session_count: 2 };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects flat sessions that disagree with nested rendered sessions", () => {
    const invalidDesiredAssignment = {
      ...desiredAssignment,
      matched_sessions: [{ ...session, room_code: "999-Z" }],
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects sessions whose LHP code disagrees with their schedule", () => {
    const invalidDesiredAssignment = {
      ...desiredAssignment,
      lhp_codes: ["INT2213-02"],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        lhp_code: "INT2213-02",
      }],
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects root assignments that differ from the first solution", () => {
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [{ ...desiredAssignment, course_name: "Khác" }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects blank session IDs", () => {
    const invalidSession = { ...session, session_id: " " };
    const invalidDesiredAssignment = {
      ...desiredAssignment,
      matched_sessions: [invalidSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        matched_sessions: [invalidSession],
      }],
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it.each([
    String.fromCharCode(0xD800),
    String.fromCharCode(0xDC00),
    "\uFFFD",
  ])("rejects malformed UTF-16 session ID %j", (sessionId) => {
    const invalidSession = { ...session, session_id: sessionId };
    const invalidDesiredAssignment = {
      ...desiredAssignment,
      matched_sessions: [invalidSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        matched_sessions: [invalidSession],
      }],
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });

  it("rejects duplicate rendered session IDs", () => {
    const duplicateSession = { ...session };
    const invalidDesiredAssignment = {
      ...desiredAssignment,
      session_count: 2,
      matched_sessions: [session, duplicateSession],
      lhp_schedules: [{
        ...desiredAssignment.lhp_schedules[0],
        session_count: 2,
        matched_sessions: [session, duplicateSession],
      }],
    };
    const invalidTimetable = {
      ...timetable,
      desired_assignments: [invalidDesiredAssignment],
      solutions: [{ ...timetable.solutions[0], desired_assignments: [invalidDesiredAssignment] }],
    };

    expect(parseTimetableDocument(JSON.stringify(documentWith({ timetable: invalidTimetable })))).toEqual({
      ok: false,
      code: "invalid-timetable",
    });
  });
});
