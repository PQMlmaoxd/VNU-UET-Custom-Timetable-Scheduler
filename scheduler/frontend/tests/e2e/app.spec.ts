import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

const validateResponse = {
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

const solveResponse = {
  mode: "reschedule",
  workbook_path: "timetable.xlsx",
  department: "ALL",
  parse_summary: { ...validateResponse.parse_summary, requested_assignments: 1 },
  prototype_catalog: validateResponse.prototype_catalog,
  desired_assignments: [],
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
      movement_cost: 2,
      desired_assignments: [
        {
          course_code: "INT2213",
          course_name: "Mạng Máy Tính",
          teaching_team_key: "team-nguyen-a",
          teaching_team_label: "Nguyễn A",
          lhp_codes: ["INT2213-01"],
          session_count: 2,
          matched_sessions: [],
          lhp_schedules: [
            {
              lhp_code: "INT2213-01",
              session_count: 2,
              matched_sessions: [
                {
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
                },
                {
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
                },
              ],
            },
          ],
        },
      ],
    },
  ],
};

test.beforeEach(async ({ page }) => {
  await page.addInitScript(({ validate, solve }) => {
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
                : {};
          const response = {
            protocol_version: 1,
            id: request.id,
            ok: true,
            result,
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
  }, { validate: validateResponse, solve: solveResponse });
});

test("happy path is keyboard-readable and has no critical/serious axe issues", async ({ page }, testInfo) => {
  await page.goto("/app/");
  await page.setInputFiles('input[type="file"]', {
    name: "timetable.xlsx",
    mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    buffer: Buffer.from("mock workbook"),
  });

  await page.getByRole("button", { name: "Đọc thời khóa biểu" }).click();
  await expect(page.getByRole("heading", { name: /Chọn môn và nhóm giảng dạy/ })).toBeVisible();

  await page.getByPlaceholder(/Tìm theo mã hoặc tên môn/).fill("mang may");
  await page.getByRole("option", { name: /INT2213/ }).click();
  await expect(page.getByRole("combobox", { name: "Nhóm giảng dạy" })).toHaveValue("team-nguyen-a");

  await page.getByRole("button", { name: "Tiếp tục" }).click();
  await page.getByRole("button", { name: "Tìm thời khóa biểu" }).click();

  await expect(page.getByRole("heading", { name: "Đã tìm thấy lịch" })).toBeVisible();

  const solveRequest = await page.evaluate(() => {
    const bridgeWindow = window as Window & {
      __schedulerBridgeRequests?: Array<{ method: string; payload?: { desired_assignments?: Array<Record<string, string>> } }>;
    };
    return bridgeWindow.__schedulerBridgeRequests?.find(request => request.method === "solve_workbook");
  });
  expect(solveRequest?.payload?.desired_assignments?.[0]).toMatchObject({
    course_code: "INT2213",
    teaching_team_key: "team-nguyen-a",
    teaching_team_label: "Nguyễn A",
  });

  await expect(page.getByLabel("Buổi học trực tuyến").getByText("Trực tuyến")).toBeVisible();
  if (testInfo.project.name === "chromium-mobile") {
    await expect(page.getByLabel("Lịch theo ngày")).toContainText("INT2213");
  } else {
    await expect(page.getByRole("button", { name: /INT2213, INT2213-01/ })).toBeVisible();
  }

  const accessibilityScanResults = await new AxeBuilder({ page }).analyze();
  const seriousViolations = accessibilityScanResults.violations.filter((violation) =>
    violation.impact === "critical" || violation.impact === "serious",
  );
  expect(seriousViolations).toEqual([]);
});

test("persists dark theme and keeps the laptop workspace bounded", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === "chromium-mobile", "Wide-screen geometry is covered by the desktop project.");
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto("/app/");

  await page.getByLabel("Giao diện").selectOption("dark");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await page.reload();
  await expect(page.getByLabel("Giao diện")).toHaveValue("dark");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");

  const laptopGeometry = await page.evaluate(() => ({
    appWidth: document.querySelector(".app-shell")?.getBoundingClientRect().width ?? 0,
    horizontalOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
  }));
  expect(laptopGeometry.appWidth).toBeLessThanOrEqual(1440);
  expect(laptopGeometry.horizontalOverflow).toBe(false);

  await page.setViewportSize({ width: 1920, height: 1080 });
  const wideGeometry = await page.evaluate(() => ({
    appWidth: document.querySelector(".app-shell")?.getBoundingClientRect().width ?? 0,
    horizontalOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
  }));
  expect(wideGeometry.appWidth).toBeLessThanOrEqual(1440);
  expect(wideGeometry.horizontalOverflow).toBe(false);

  const darkThemeScan = await new AxeBuilder({ page }).analyze();
  const seriousDarkThemeViolations = darkThemeScan.violations.filter((violation) =>
    violation.impact === "critical" || violation.impact === "serious",
  );
  expect(seriousDarkThemeViolations).toEqual([]);
});
