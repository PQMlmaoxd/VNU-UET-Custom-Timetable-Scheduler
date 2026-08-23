import { afterEach, describe, expect, it, vi } from "vitest";

import {
  exportUnsatArtifact,
  isRescheduleResponse,
  isTimetableTooLargeError,
  MAX_DESKTOP_MESSAGE_CHARACTERS,
  notifyDesktopReady,
  saveTimetable,
  setDesktopTheme,
  solveWorkbook,
  validateWorkbook,
} from "./api";
import type { RescheduleResponse } from "./types";

type DesktopMessageListener = (event: MessageEvent<unknown>) => void;

class FakeDesktopWebView {
  readonly messages: unknown[] = [];
  private readonly listeners = new Set<DesktopMessageListener>();

  addEventListener(event: "message", listener: DesktopMessageListener) {
    if (event === "message") {
      this.listeners.add(listener);
    }
  }

  removeEventListener(event: "message", listener: DesktopMessageListener) {
    if (event === "message") {
      this.listeners.delete(listener);
    }
  }

  postMessage(message: unknown) {
    this.messages.push(message);
  }

  respond(message: unknown) {
    for (const listener of this.listeners) {
      listener(new MessageEvent("message", { data: message }));
    }
  }
}

const originalChrome = Object.getOwnPropertyDescriptor(window, "chrome");

const validateResponse = {
  mode: "validate_existing",
  workbook_path: "fixture.xlsx",
  department: "ALL",
  parse_summary: {
    sessions: 1,
    schedulable_sessions: 1,
    online_sessions: 0,
    other_department_sessions: 0,
    lecturer_blocks: 0,
    anchor_count: 1,
    requested_assignments: 0,
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
      course_code: "INT1000",
      course_name: "Programming",
      teaching_team_key: "team-alice",
      teaching_team_label: "Alice",
      session_count: 1,
    }],
    room_cost_rules: [{
      from_zone: "A",
      to_zone: "A",
      cost: 1,
      description: "Khác phòng trong cùng tòa nhà",
    }],
  },
  existing_schedule_validation: {
    is_valid: true,
    is_complete: true,
    violation_count: 0,
    missing_session_count: 0,
    sample_violations: [],
  },
};

const partialTimeoutSession = {
  session_id: "row_1",
  course_code: "INT1000",
  course_name: "Programming",
  lhp_code: "LHP-1",
  session_type: "LT",
  lecturer_names: ["Alice"],
  cohort_codes: ["K69I"],
  timeslot_label: "Thứ 2 Ca 1",
  room_code: "101-A",
  source_row: 1,
  day: 2,
  period_code: "1",
  period_atomic: ["1"],
};

const partialTimeoutAssignment = {
  course_code: "INT1000",
  teaching_team_key: "team-alice",
  teaching_team_label: "Alice",
  course_name: "Programming",
  lhp_codes: ["LHP-1"],
  session_count: 1,
  matched_sessions: [partialTimeoutSession],
  lhp_schedules: [{
    lhp_code: "LHP-1",
    session_count: 1,
    matched_sessions: [partialTimeoutSession],
  }],
};

const partialTimeoutResponse = {
  ...validateResponse,
  mode: "reschedule",
  department: "CNTT",
  desired_assignments: [partialTimeoutAssignment],
  solutions: [{
    solution_index: 1,
    movement_cost: 0,
    desired_assignments: [partialTimeoutAssignment],
  }],
  solver: {
    backend: "personal_sat",
    status: "timeout",
    satisfiability: "UNKNOWN",
    solve_time_ms: 30_000,
    objective_value: null,
    assignment_count: 1,
    solution_count: 1,
    solver_info: "fixture",
    explanation: ["Tìm lịch đã hết thời gian cho phép."],
    formal_verification_token: null,
  },
  solved_schedule_validation: null,
};

afterEach(() => {
  vi.restoreAllMocks();
  if (originalChrome) {
    Object.defineProperty(window, "chrome", originalChrome);
    return;
  }

  Reflect.deleteProperty(window, "chrome");
});

describe("desktop bridge API", () => {
  it("accepts timed-out solve responses that contain partial solutions", () => {
    expect(isRescheduleResponse(partialTimeoutResponse)).toBe(true);
  });

  it("identifies oversized timetable errors without matching unrelated failures", () => {
    expect(isTimetableTooLargeError(new Error("Desktop bridge request is too large."))).toBe(true);
    expect(isTimetableTooLargeError(new Error("Thời khóa biểu JSON vượt quá giới hạn 25 MB."))).toBe(true);
    expect(isTimetableTooLargeError(new Error("Hãy thử chọn vị trí lưu khác."))).toBe(false);
  });

  it("requires the desktop bridge for workbook operations", async () => {
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;

    await expect(validateWorkbook(workbook)).rejects.toThrow("Không thể kết nối với ứng dụng máy tính.");
  });

  it("accepts the serialized validate response emitted by the desktop bridge", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const validation = validateWorkbook(workbook);

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond(JSON.stringify({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: validateResponse,
      error: null,
    }));

    await expect(validation).resolves.toMatchObject(validateResponse);
  });

  it("cancels an active desktop workbook validation when the caller aborts", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const cancellation = new AbortController();
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const validation = validateWorkbook(workbook, cancellation.signal);

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    cancellation.abort();

    await expect(validation).rejects.toMatchObject({ name: "AbortError" });
    expect(webView.messages).toHaveLength(2);
    expect(webView.messages[0]).toMatchObject({ method: "validate_workbook" });
    expect(webView.messages[1]).toMatchObject({
      method: "cancel_command",
      payload: { target_id: (webView.messages[0] as { id: string }).id },
    });
  });

  it("cancels an active desktop solve command when the caller aborts", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const cancellation = new AbortController();
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const solve = solveWorkbook(
      workbook,
      [{ course_code: "INT1000", course_name: "Programming", teaching_team_key: "team-alice", teaching_team_label: "Alice" }],
      30,
      cancellation.signal,
    );

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    cancellation.abort();

    await expect(solve).rejects.toMatchObject({ name: "AbortError" });
    expect(webView.messages).toHaveLength(2);
    expect(webView.messages[0]).toMatchObject({ method: "solve_workbook" });
    expect(webView.messages[1]).toMatchObject({
      method: "cancel_command",
      payload: { target_id: (webView.messages[0] as { id: string }).id },
    });
  });

  it("sends theme and readiness messages through the desktop bridge", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });

    const theme = setDesktopTheme("dark");
    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    expect(webView.messages[0]).toMatchObject({ method: "set_theme", payload: { preference: "dark" } });
    const themeRequest = webView.messages[0] as { id: string };
    webView.respond({ protocol_version: 1, id: themeRequest.id, ok: true, result: { preference: "dark" }, error: null });
    await expect(theme).resolves.toBeUndefined();

    const ready = notifyDesktopReady();
    await vi.waitFor(() => expect(webView.messages).toHaveLength(2));
    expect(webView.messages[1]).toMatchObject({ method: "desktop_ready" });
    const readyRequest = webView.messages[1] as { id: string };
    webView.respond({ protocol_version: 1, id: readyRequest.id, ok: true, result: { ready: true }, error: null });
    await expect(ready).resolves.toBeUndefined();
  });

  it("exports an UNSAT artifact through the desktop bridge", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const exportRequest = exportUnsatArtifact(workbook, [
      { course_code: "INT1000", course_name: "Programming", teaching_team_key: "team-alice", teaching_team_label: "Alice" },
    ], "verification-token");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as {
      id: string;
      method: string;
      payload: { desired_assignments: unknown[]; verification_token: string };
    };
    expect(request.method).toBe("export_unsat_artifact");
    expect(request.payload.desired_assignments).toHaveLength(1);
    expect(request.payload).toMatchObject({ verification_token: "verification-token" });
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: {
        exported: true,
        file_name: "unsat-verification.zip",
        cnf_sha256: "A".repeat(64),
        variable_count: 1,
        clause_count: 1,
      },
      error: null,
    });

    await expect(exportRequest).resolves.toMatchObject({ exported: true });
  });

  it("returns a native dialog cancellation from UNSAT artifact export", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const exportRequest = exportUnsatArtifact(workbook, [
      { course_code: "INT1000", course_name: "Programming", teaching_team_key: "team-alice", teaching_team_label: "Alice" },
    ], "verification-token");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: {
        exported: false,
        file_name: null,
        cnf_sha256: null,
        variable_count: null,
        clause_count: null,
      },
      error: null,
    });

    await expect(exportRequest).resolves.toEqual({
      exported: false,
      file_name: null,
      cnf_sha256: null,
      variable_count: null,
      clause_count: null,
    });
  });

  it("rejects malformed UNSAT artifact response metadata", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const exportRequest = exportUnsatArtifact(workbook, [
      { course_code: "INT1000", course_name: "Programming", teaching_team_key: "team-alice", teaching_team_label: "Alice" },
    ], "verification-token");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: {
        exported: true,
        file_name: "unsat-\uFFFD.zip",
        cnf_sha256: "A".repeat(64),
        variable_count: 1,
        clause_count: 1,
      },
      error: null,
    });

    await expect(exportRequest).rejects.toThrow("Desktop bridge returned an invalid result.");
  });

  it.each([
    { file_name: " ", cnf_sha256: "A".repeat(64), variable_count: 1, clause_count: 1 },
    { file_name: "unsat.zip", cnf_sha256: "not-a-sha256", variable_count: 1, clause_count: 1 },
    { file_name: null, cnf_sha256: null, variable_count: null, clause_count: null },
  ])("rejects incoherent successful UNSAT artifact metadata", async (metadata) => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const exportRequest = exportUnsatArtifact(workbook, [
      { course_code: "INT1000", course_name: "Programming", teaching_team_key: "team-alice", teaching_team_label: "Alice" },
    ], "verification-token");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: { exported: true, ...metadata },
      error: null,
    });

    await expect(exportRequest).rejects.toThrow("Desktop bridge returned an invalid result.");
  });

  it("rejects metadata on a cancelled UNSAT artifact export", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const exportRequest = exportUnsatArtifact(workbook, [
      { course_code: "INT1000", course_name: "Programming", teaching_team_key: "team-alice", teaching_team_label: "Alice" },
    ], "verification-token");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: {
        exported: false,
        file_name: "unsat.zip",
        cnf_sha256: "A".repeat(64),
        variable_count: 1,
        clause_count: 1,
      },
      error: null,
    });

    await expect(exportRequest).rejects.toThrow("Desktop bridge returned an invalid result.");
  });

  it("saves a SAT timetable through the desktop bridge", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const timetable = validateResponse as unknown as RescheduleResponse;
    const saveRequest = saveTimetable(timetable, "schedule.xlsx");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as {
      id: string;
      method: string;
      payload: { timetable: unknown; source_file_name: string };
    };
    expect(request.method).toBe("save_timetable");
    expect(request.payload.timetable).toBe(timetable);
    expect(request.payload.source_file_name).toBe("schedule.xlsx");
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: { saved: true, file_name: "TKB-CNTT-20260821-1530.json" },
      error: null,
    });

    await expect(saveRequest).resolves.toEqual({
      saved: true,
      file_name: "TKB-CNTT-20260821-1530.json",
    });
  });

  it("returns a native dialog cancellation from timetable save", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const saveRequest = saveTimetable({} as RescheduleResponse, "schedule.json");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: { saved: false, file_name: null },
      error: null,
    });

    await expect(saveRequest).resolves.toEqual({ saved: false, file_name: null });
  });

  it("rejects a malformed timetable save response", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const saveRequest = saveTimetable({} as RescheduleResponse, "schedule.json");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: { saved: "yes", file_name: null },
      error: null,
    });

    await expect(saveRequest).rejects.toThrow("Desktop bridge returned an invalid result.");
  });

  it("rejects a malformed timetable save file name", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const saveRequest = saveTimetable({} as RescheduleResponse, "schedule.json");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: { saved: true, file_name: "saved-\uFFFD.json" },
      error: null,
    });

    await expect(saveRequest).rejects.toThrow("Desktop bridge returned an invalid result.");
  });

  it.each([
    { saved: true, file_name: " " },
    { saved: false, file_name: "saved.json" },
  ])("rejects incoherent timetable save metadata", async (result) => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const saveRequest = saveTimetable({} as RescheduleResponse, "schedule.json");

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result,
      error: null,
    });

    await expect(saveRequest).rejects.toThrow("Desktop bridge returned an invalid result.");
  });

  it("cancels a desktop command when the bridge timeout expires", async () => {
    vi.useFakeTimers();
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const validation = validateWorkbook(workbook);

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    vi.advanceTimersByTime(60_000);

    await expect(validation).rejects.toThrow("Desktop bridge timed out.");
    expect(webView.messages).toHaveLength(2);
    expect(webView.messages[1]).toMatchObject({
      method: "cancel_command",
      payload: { target_id: (webView.messages[0] as { id: string }).id },
    });
    vi.useRealTimers();
  });

  it("rejects malformed responses for the active bridge request", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const validation = validateWorkbook(workbook);

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({ protocol_version: 1, id: request.id, ok: "true", result: null, error: null });

    await expect(validation).rejects.toThrow("invalid response");
  });

  it("rejects malformed errors for the active bridge request", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const validation = validateWorkbook(workbook);

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({ protocol_version: 1, id: request.id, ok: false, result: null, error: "invalid-\uFFFD" });

    await expect(validation).rejects.toThrow("invalid response");
  });

  it.each([
    { ok: true, result: validateResponse, error: "contradictory error" },
    { ok: false, result: validateResponse, error: "failed" },
    { ok: false, result: null, error: " " },
  ])("rejects contradictory desktop bridge envelopes", async (envelope) => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const validation = validateWorkbook(workbook);

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ...envelope,
    });

    await expect(validation).rejects.toThrow("invalid response");
  });

  it("rejects a protocol mismatch for the active bridge request immediately", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const validation = validateWorkbook(workbook);

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({ protocol_version: 99, id: request.id, ok: true, result: {}, error: null });

    await expect(validation).rejects.toThrow("protocol version is incompatible");
  });

  it("rejects an incomplete solve result before rendering can consume it", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "schedule.xlsx",
      arrayBuffer: async () => new TextEncoder().encode("workbook").buffer,
    } as File;
    const solve = solveWorkbook(
      workbook,
      [{ course_code: "INT1000", course_name: "Programming", teaching_team_key: "team-alice", teaching_team_label: "Alice" }],
      30,
    );

    await vi.waitFor(() => expect(webView.messages).toHaveLength(1));
    const request = webView.messages[0] as { id: string };
    webView.respond({
      protocol_version: 1,
      id: request.id,
      ok: true,
      result: {
        mode: "reschedule",
        workbook_path: "schedule.xlsx",
        department: "ALL",
        parse_summary: {},
        prototype_catalog: { anchors: [], room_cost_rules: [] },
        desired_assignments: [],
        solutions: [{}],
        existing_schedule_validation: {},
        solver: { satisfiability: "SAT" },
        solved_schedule_validation: null,
      },
      error: null,
    });

    await expect(solve).rejects.toThrow("invalid result");
  });

  it("rejects oversized workbooks before reading their bytes", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const workbook = {
      name: "large.xlsx",
      size: 25 * 1024 * 1024 + 1,
      arrayBuffer: vi.fn(),
    } as unknown as File;

    await expect(validateWorkbook(workbook)).rejects.toThrow("25 MB");
    expect(workbook.arrayBuffer).not.toHaveBeenCalled();
    expect(webView.messages).toHaveLength(0);
  });

  it("rejects oversized bridge requests before posting them", async () => {
    const webView = new FakeDesktopWebView();
    Object.defineProperty(window, "chrome", {
      configurable: true,
      value: { webview: webView },
    });
    const originalStringify = JSON.stringify.bind(JSON);
    vi.spyOn(JSON, "stringify").mockImplementation((value) => {
      if (typeof value === "object" && value !== null
        && (value as { method?: unknown }).method === "save_timetable") {
        return { length: MAX_DESKTOP_MESSAGE_CHARACTERS + 1 } as unknown as string;
      }
      return originalStringify(value);
    });

    await expect(saveTimetable({} as RescheduleResponse, "schedule.json"))
      .rejects.toThrow("Desktop bridge request is too large.");
    expect(webView.messages).toHaveLength(0);
  });
});
