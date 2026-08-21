import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import App from "./App";
import { COPY } from "./copy";

type DesktopMessage = {
  id: string;
  method: string;
};

class FakeDesktopWebView {
  readonly messages: unknown[] = [];
  private readonly listeners = new Set<(event: MessageEvent<unknown>) => void>();

  constructor(
    private readonly autoRespond: boolean,
    private readonly response: unknown = workbookResponse,
    private readonly commandResults: Record<string, unknown> = {},
  ) {}

  addEventListener(_event: "message", listener: (event: MessageEvent<unknown>) => void) {
    this.listeners.add(listener);
  }

  removeEventListener(_event: "message", listener: (event: MessageEvent<unknown>) => void) {
    this.listeners.delete(listener);
  }

  postMessage(message: unknown) {
    this.messages.push(message);
    const request = message as DesktopMessage;
    if (!this.autoRespond) {
      return;
    }

    const result = request.method === "validate_workbook"
      ? this.response
      : this.commandResults[request.method] ?? {};
    this.respond({ protocol_version: 1, id: request.id, ok: true, result });
  }

  respond(data: unknown) {
    for (const listener of this.listeners) {
      listener(new MessageEvent("message", { data }));
    }
  }
}

const workbookResponse = {
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

const partialWorkbookResponse = {
  ...workbookResponse,
  parse_summary: {
    ...workbookResponse.parse_summary,
    partial_import: true,
    quarantined_lhp_count: 1,
    quarantined_session_count: 1,
    quarantined_offerings: [{
      course_code: "INT2213",
      lhp_code: "INT2213-02",
      reason_code: "unresolved_physical_schedule",
      source_locations: ["TKB K67 - K70 row 790"],
      quarantined_row_count: 1,
      excluded_session_count: 1,
    }],
  },
};

const originalChrome = Object.getOwnPropertyDescriptor(window, "chrome");

function makeTimetableFile(contents: string, name: string): File {
  const file = new File([contents], name, {
    type: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
  });
  Object.defineProperty(file, "arrayBuffer", {
    value: async () => new TextEncoder().encode(contents).buffer,
  });
  return file;
}

function mockDesktopBridge(
  autoRespond = true,
  response: unknown = workbookResponse,
  commandResults: Record<string, unknown> = {},
) {
  const webView = new FakeDesktopWebView(autoRespond, response, commandResults);
  Object.defineProperty(window, "chrome", {
    configurable: true,
    value: { webview: webView },
  });
  return webView;
}

const savedSession = {
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

const savedDesiredAssignment = {
  course_code: "INT2213",
  teaching_team_key: "team-nguyen-a",
  teaching_team_label: "Nguyễn A",
  course_name: "Mạng Máy Tính",
  lhp_codes: ["INT2213-01"],
  session_count: 1,
  matched_sessions: [savedSession],
  lhp_schedules: [{
    lhp_code: "INT2213-01",
    session_count: 1,
    matched_sessions: [savedSession],
  }],
};

const savedTimetableResponse = {
  mode: "reschedule",
  workbook_path: "timetable.xlsx",
  department: "CNTT",
  parse_summary: { ...workbookResponse.parse_summary, requested_assignments: 1 },
  prototype_catalog: workbookResponse.prototype_catalog,
  desired_assignments: [savedDesiredAssignment],
  existing_schedule_validation: workbookResponse.existing_schedule_validation,
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
    desired_assignments: [savedDesiredAssignment],
  }],
};

function makeSavedTimetableFile(
  name = "saved-timetable.json",
  overrides: Record<string, unknown> = {},
): File {
  const document = {
    schema: "vnu-uet-timetable",
    schema_version: 1,
    saved_at_utc: "2026-08-21T08:30:00Z",
    app_version: "1.0.1",
    source: { file_name: "timetable.xlsx" },
    timetable: savedTimetableResponse,
    ...overrides,
  };
  const contents = JSON.stringify(document);
  const file = new File([contents], name, { type: "application/json" });
  Object.defineProperty(file, "text", { value: async () => contents });
  return file;
}

function makeInvalidJsonFile(name = "broken.json"): File {
  const file = new File(["not json"], name, { type: "application/json" });
  Object.defineProperty(file, "text", { value: async () => "not json" });
  return file;
}

afterEach(() => {
  vi.restoreAllMocks();
  if (originalChrome) {
    Object.defineProperty(window, "chrome", originalChrome);
  } else {
    Reflect.deleteProperty(window, "chrome");
  }
});

describe("App", () => {
  it("starts with an accessible upload flow", () => {
    render(<App />);

    expect(screen.getByRole("heading", { name: COPY.product.title })).toBeInTheDocument();
    expect(document.querySelector("img.product-mark")).toHaveAttribute("width", "42");
    expect(document.querySelector("img.product-mark")).toHaveAttribute("height", "32");
    expect(screen.getByLabelText(COPY.upload.inputLabel)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: COPY.upload.readButton })).toBeDisabled();
    expect(document.body.textContent).not.toMatch(/workbook|solver|backend|parser|native worker|SAT selector|nghiệm|tổ hợp|ràng buộc/i);
  });

  it("rejects non-xlsx files before calling the API", async () => {
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    fireEvent.change(input, {
      target: { files: [new File(["not excel"], "notes.txt", { type: "text/plain" })] },
    });

    expect(screen.getByText(COPY.upload.invalidTitle)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: COPY.upload.readButton })).toBeDisabled();
  });

  it("loads workbook catalog and opens the selection step", async () => {
    const user = userEvent.setup();
    mockDesktopBridge();

    render(<App />);
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeTimetableFile("xlsx", "timetable.xlsx"));
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: COPY.selection.heading })).toBeInTheDocument();
    });
    expect(document.body.textContent).not.toMatch(/workbook|solver|backend|parser|native worker|SAT selector|nghiệm|tổ hợp|ràng buộc/i);
  });

  it("loads a saved JSON timetable directly into the results step", async () => {
    const user = userEvent.setup();
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeSavedTimetableFile());
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: COPY.results.validHeading })).toBeInTheDocument();
    });
    expect(screen.queryByRole("heading", { name: COPY.selection.heading })).not.toBeInTheDocument();
    expect(screen.queryByText(COPY.results.loadedSelectionDetail)).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /INT2213, INT2213-01/ })).toBeInTheDocument();
  });

  it("shows partial-import details when a SAT JSON timetable is reopened", async () => {
    const user = userEvent.setup();
    render(<App />);

    const partialTimetable = {
      ...savedTimetableResponse,
      parse_summary: {
        ...savedTimetableResponse.parse_summary,
        partial_import: true,
        quarantined_lhp_count: 1,
      },
    };
    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeSavedTimetableFile("partial.json", { timetable: partialTimetable }));
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));

    await screen.findByRole("heading", { name: COPY.results.validHeading });
    expect(screen.getByText(COPY.results.partialImportDetail(1))).toBeInTheDocument();
  });

  it("rejects an invalid saved JSON file without leaving the upload step", async () => {
    const user = userEvent.setup();
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeInvalidJsonFile());
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));

    await waitFor(() => expect(screen.getByText("File không phải là JSON hợp lệ.")).toBeInTheDocument());
    expect(screen.getByRole("heading", { name: COPY.upload.heading })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: COPY.results.validHeading })).not.toBeInTheDocument();
  });

  it("rejects a saved JSON document from a newer schema version", async () => {
    const user = userEvent.setup();
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeSavedTimetableFile("future.json", { schema_version: 2 }));
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));

    await waitFor(() => expect(screen.getByText(COPY.upload.jsonNewerDetail)).toBeInTheDocument());
    expect(screen.getByRole("heading", { name: COPY.upload.heading })).toBeInTheDocument();
  });

  it("saves a loaded SAT timetable through the desktop bridge", async () => {
    const user = userEvent.setup();
    const webView = mockDesktopBridge(true, workbookResponse, {
      save_timetable: { saved: true, file_name: "TKB-CNTT-20260821-1530.json" },
    });
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeSavedTimetableFile());
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));
    await screen.findByRole("heading", { name: COPY.results.validHeading });
    await user.click(screen.getByRole("button", { name: COPY.results.saveTimetable }));

    await waitFor(() => expect(screen.getByText(COPY.results.savedTimetableDetail("TKB-CNTT-20260821-1530.json"))).toBeInTheDocument());
    const request = webView.messages.find((message) => (message as DesktopMessage).method === "save_timetable") as {
      method: string;
      payload: { source_file_name: string };
    };
    expect(request.method).toBe("save_timetable");
    expect(request.payload.source_file_name).toBe("timetable.xlsx");
  });

  it("ignores a stale save response after choosing another file", async () => {
    const user = userEvent.setup();
    const webView = mockDesktopBridge(false);
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeSavedTimetableFile());
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));
    await screen.findByRole("heading", { name: COPY.results.validHeading });
    await user.click(screen.getByRole("button", { name: COPY.results.saveTimetable }));

    await waitFor(() => {
      expect(webView.messages.some((message) => (message as DesktopMessage).method === "save_timetable")).toBe(true);
    });
    const saveRequest = webView.messages.find(
      (message) => (message as DesktopMessage).method === "save_timetable",
    ) as DesktopMessage;
    await user.click(screen.getByRole("button", { name: COPY.results.chooseAnotherFile }));
    webView.respond({
      protocol_version: 1,
      id: saveRequest.id,
      ok: true,
      result: { saved: true, file_name: "stale.json" },
    });

    await waitFor(() => expect(screen.getByRole("heading", { name: COPY.upload.heading })).toBeInTheDocument());
    expect(screen.queryByText(COPY.results.savedTimetableDetail("stale.json"))).not.toBeInTheDocument();
  });

  it("does not show the desktop save action in browser mode", async () => {
    const user = userEvent.setup();
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeSavedTimetableFile());
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));
    await screen.findByRole("heading", { name: COPY.results.validHeading });

    expect(screen.queryByRole("button", { name: COPY.results.saveTimetable })).not.toBeInTheDocument();
  });

  it("resets a loaded timetable when another file is selected", async () => {
    const user = userEvent.setup();
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeSavedTimetableFile());
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));
    await screen.findByRole("heading", { name: COPY.results.validHeading });
    await user.click(screen.getByRole("button", { name: COPY.results.chooseAnotherFile }));

    expect(screen.getByRole("heading", { name: COPY.upload.heading })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: COPY.results.validHeading })).not.toBeInTheDocument();
  });

  it("clears a committed course when its combobox text is edited", async () => {
    const user = userEvent.setup();
    mockDesktopBridge();
    render(<App />);

    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(fileInput, makeTimetableFile("xlsx", "timetable.xlsx"));
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));
    const courseInput = await screen.findByPlaceholderText(COPY.selection.coursePlaceholder);

    await user.click(courseInput);
    await user.click(screen.getByRole("option", { name: /INT2213/i }));
    expect(screen.getByLabelText("Nhóm giảng dạy")).toHaveValue("team-nguyen-a");

    await user.clear(courseInput);
    await user.type(courseInput, "khac");

    expect(screen.getByLabelText("Nhóm giảng dạy")).toBeDisabled();
    expect(screen.getByRole("button", { name: COPY.selection.continue })).toBeDisabled();
  });

  it("blocks continuation when any selection row is incomplete", async () => {
    const user = userEvent.setup();
    mockDesktopBridge();
    render(<App />);

    const fileInput = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(fileInput, makeTimetableFile("xlsx", "timetable.xlsx"));
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));
    const courseInput = await screen.findByPlaceholderText(COPY.selection.coursePlaceholder);
    await user.click(courseInput);
    await user.click(screen.getByRole("option", { name: /INT2213/i }));
    await user.click(screen.getByRole("button", { name: /Thêm môn/i }));

    expect(screen.getByText(COPY.selection.incomplete)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: COPY.selection.continue })).toBeDisabled();
  });

  it("shows partial import details without blocking complete selections", async () => {
    const user = userEvent.setup();
    mockDesktopBridge(true, partialWorkbookResponse);
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeTimetableFile("xlsx", "timetable.xlsx"));
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));

    await screen.findByRole("heading", { name: COPY.selection.heading });
    expect(screen.getByText(COPY.selection.partialImport(1))).toBeInTheDocument();
    expect(screen.getByText(/INT2213 · INT2213-02/)).toBeInTheDocument();

    const courseInput = screen.getByPlaceholderText(COPY.selection.coursePlaceholder);
    await user.click(courseInput);
    await user.click(screen.getByRole("option", { name: /INT2213/i }));

    expect(screen.getByRole("button", { name: COPY.selection.continue })).toBeEnabled();
  });

  it("ignores a stale validation response after the workbook changes", async () => {
    const user = userEvent.setup();
    const webView = mockDesktopBridge(false);
    render(<App />);

    const input = document.querySelector('input[type="file"]') as HTMLInputElement;
    await user.upload(input, makeTimetableFile("first", "first.xlsx"));
    await user.click(screen.getByRole("button", { name: COPY.upload.readButton }));
    await user.upload(input, makeTimetableFile("second", "second.xlsx"));
    const firstRequest = webView.messages.find(
      (message) => (message as DesktopMessage).method === "validate_workbook",
    ) as DesktopMessage;
    webView.respond({ protocol_version: 1, id: firstRequest.id, ok: true, result: workbookResponse });

    await waitFor(() => {
      expect(screen.getByLabelText(COPY.product.currentFile)).toHaveTextContent("second.xlsx");
    });
    expect(screen.getByRole("button", { name: COPY.upload.readButton })).toBeEnabled();
    expect(screen.queryByRole("heading", { name: COPY.selection.heading })).not.toBeInTheDocument();
  });
});
