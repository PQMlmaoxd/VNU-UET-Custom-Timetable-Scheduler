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
