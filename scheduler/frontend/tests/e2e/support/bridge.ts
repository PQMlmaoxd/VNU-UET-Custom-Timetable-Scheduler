import type { Page } from "@playwright/test";

export const validateResponse = {
  mode: "validate_existing",
  workbook_path: "timetable.xlsx",
  department: "ALL",
  parse_summary: {
    sessions: 3,
    schedulable_sessions: 2,
    online_sessions: 1,
    other_department_sessions: 0,
    lecturer_blocks: 0,
    anchor_count: 1,
    requested_assignments: 0,
    rooms: 2,
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
    anchors: [
      {
        course_code: "INT2213",
        course_name: "Mạng Máy Tính",
        teaching_team_key: "team-nguyen-a",
        teaching_team_label: "Nguyễn A",
        session_count: 2,
      },
    ],
    room_cost_rules: [],
  },
  existing_schedule_validation: {
    is_valid: true,
    is_complete: true,
    violation_count: 0,
    missing_session_count: 0,
    sample_violations: [],
  },
};

export const solveSessionOne = {
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

export const solveSessionTwo = {
  session_id: "row_2",
  course_code: "INT2213",
  course_name: "Mạng Máy Tính",
  lhp_code: "INT2213-01",
  session_type: "ONL",
  lecturer_names: ["Nguyễn A"],
  cohort_codes: ["K69I"],
  timeslot_label: "Online",
  room_code: "ONL",
  source_row: 2,
  day: null,
  period_code: null,
  period_atomic: null,
};

export const solveDesiredAssignment = {
  course_code: "INT2213",
  course_name: "Mạng Máy Tính",
  teaching_team_key: "team-nguyen-a",
  teaching_team_label: "Nguyễn A",
  lhp_codes: ["INT2213-01"],
  session_count: 2,
  matched_sessions: [solveSessionOne, solveSessionTwo],
  lhp_schedules: [
    {
      lhp_code: "INT2213-01",
      session_count: 2,
      matched_sessions: [solveSessionOne, solveSessionTwo],
    },
  ],
};

export const solveResponse = {
  mode: "reschedule",
  workbook_path: "timetable.xlsx",
  department: "ALL",
  parse_summary: { ...validateResponse.parse_summary, requested_assignments: 1 },
  prototype_catalog: validateResponse.prototype_catalog,
  desired_assignments: [solveDesiredAssignment],
  existing_schedule_validation: validateResponse.existing_schedule_validation,
  solved_schedule_validation: validateResponse.existing_schedule_validation,
  solver: {
    backend: "personal_sat",
    status: "feasible",
    satisfiability: "SAT",
    solve_time_ms: 25,
    objective_value: null,
    assignment_count: 2,
    solution_count: 1,
    solver_info: "mock SAT",
    explanation: ["Mock solution for UX regression."],
    formal_verification_token: null,
  },
  solutions: [
    {
      solution_index: 1,
      movement_cost: 0,
      desired_assignments: [solveDesiredAssignment],
    },
  ],
};

export const saveResponse = {
  saved: true,
  file_name: "TKB-ALL-20260821-1530.json",
};

export function installDesktopBridge(
  page: Page,
  fixtures: { validate?: unknown; solve?: unknown; save?: unknown } = {},
): void {
  const validate = fixtures.validate ?? validateResponse;
  const solve = fixtures.solve ?? solveResponse;
  const save = fixtures.save ?? saveResponse;

  void page.addInitScript(({ validate, solve, save }) => {
    const listeners = new Set<(event: MessageEvent<unknown>) => void>();
    const webView = {
      addEventListener: (_event: "message", listener: (event: MessageEvent<unknown>) => void) => {
        listeners.add(listener);
      },
      removeEventListener: (_event: "message", listener: (event: MessageEvent<unknown>) => void) => {
        listeners.delete(listener);
      },
      postMessage: (request: { id: string; method: string; payload?: unknown }) => {
        const bridgeWindow = window as Window & {
          __schedulerBridgeRequests?: Array<{ method: string; payload?: unknown }>;
        };
        bridgeWindow.__schedulerBridgeRequests ??= [];
        bridgeWindow.__schedulerBridgeRequests.push({ method: request.method, payload: request.payload });
        window.setTimeout(() => {
          const result = request.method === "validate_workbook"
            ? validate
            : request.method === "solve_workbook"
              ? solve
              : request.method === "cancel_command"
                ? { cancelled: true }
                : request.method === "save_timetable"
                  ? save
                : {};
          const response = {
            protocol_version: 1,
            id: request.id,
            ok: true,
            result,
            error: null,
          };
          for (const listener of listeners) {
            listener(new MessageEvent("message", { data: response }));
          }
        }, 0);
      },
    };
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
  }, { validate, solve, save });
}

export function savedTimetableJson(timetable: unknown = solveResponse): string {
  return JSON.stringify({
    schema: "vnu-uet-timetable",
    schema_version: 1,
    saved_at_utc: "2026-08-21T08:30:00Z",
    app_version: "1.0.1",
    source: { file_name: "timetable.xlsx" },
    timetable,
  });
}

export async function loadResultsFromSavedJson(page: Page): Promise<void> {
  await page.goto("/app/");
  await page.setInputFiles('input[type="file"]', {
    name: "saved-timetable.json",
    mimeType: "application/json",
    buffer: Buffer.from(savedTimetableJson()),
  });
  await page.getByRole("button", { name: "Đọc thời khóa biểu" }).click();
  await page.getByRole("heading", { name: "Đã tìm thấy lịch" }).waitFor();
}
