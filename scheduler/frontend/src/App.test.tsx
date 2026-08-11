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

    const result = request.method === "validate_workbook" ? workbookResponse : {};
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
    warnings: [],
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

function mockDesktopBridge(autoRespond = true) {
  const webView = new FakeDesktopWebView(autoRespond);
  Object.defineProperty(window, "chrome", {
    configurable: true,
    value: { webview: webView },
  });
  return webView;
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
