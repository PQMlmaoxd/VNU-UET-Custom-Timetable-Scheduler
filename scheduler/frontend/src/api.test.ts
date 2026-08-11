import { afterEach, describe, expect, it, vi } from "vitest";

import { exportUnsatArtifact, notifyDesktopReady, setDesktopTheme, solveWorkbook, validateWorkbook } from "./api";

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
    warnings: [],
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

afterEach(() => {
  vi.restoreAllMocks();
  if (originalChrome) {
    Object.defineProperty(window, "chrome", originalChrome);
    return;
  }

  Reflect.deleteProperty(window, "chrome");
});

describe("desktop bridge API", () => {
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
    webView.respond({ protocol_version: 1, id: themeRequest.id, ok: true, result: { preference: "dark" } });
    await expect(theme).resolves.toBeUndefined();

    const ready = notifyDesktopReady();
    await vi.waitFor(() => expect(webView.messages).toHaveLength(2));
    expect(webView.messages[1]).toMatchObject({ method: "desktop_ready" });
    const readyRequest = webView.messages[1] as { id: string };
    webView.respond({ protocol_version: 1, id: readyRequest.id, ok: true, result: { ready: true } });
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
    });

    await expect(exportRequest).resolves.toMatchObject({ exported: true });
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
    webView.respond({ protocol_version: 1, id: request.id, ok: "true" });

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
    webView.respond({ protocol_version: 99, id: request.id, ok: true, result: {} });

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
});
