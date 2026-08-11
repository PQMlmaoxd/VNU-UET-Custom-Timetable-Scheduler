import type { DesiredAssignmentPayload, RescheduleResponse, ValidateExistingResponse } from "./types";
import type { ThemePreference } from "./theme";

type DesktopWebView = {
  addEventListener(event: "message", listener: (event: MessageEvent<unknown>) => void): void;
  removeEventListener(event: "message", listener: (event: MessageEvent<unknown>) => void): void;
  postMessage(message: unknown): void;
};

type DesktopBridgeResponse = {
  protocol_version: number;
  id: string;
  ok: boolean;
  result?: unknown;
  error?: string | null;
};

const DESKTOP_BRIDGE_PROTOCOL_VERSION = 1;
// The bridge transfers base64 JSON, which temporarily holds several copies of a file.
// Keep the accepted desktop document size below the renderer memory budget.
const MAX_WORKBOOK_BYTES = 25 * 1024 * 1024;
const DEFAULT_COMMAND_TIMEOUT_MS = 60_000;
const SOLVE_OVERHEAD_TIMEOUT_MS = 60_000;

type ResultValidator<T> = (value: unknown) => value is T;

type SolverSummaryShape = {
  backend: string;
  status: string;
  satisfiability: string;
  solve_time_ms: number;
  objective_value: number | null;
  assignment_count: number;
  solution_count: number;
  solver_info: string;
  explanation: string[];
  formal_verification_token: string | null;
};

function getDesktopWebView(): DesktopWebView | null {
  const chrome = (window as unknown as { chrome?: { webview?: DesktopWebView } }).chrome;
  return chrome?.webview ?? null;
}

export function isDesktopApp(): boolean {
  return getDesktopWebView() !== null;
}

async function workbookPayload(
  file: File,
  signal?: AbortSignal,
): Promise<{ file_name: string; bytes_base64: string }> {
  if (file.size > MAX_WORKBOOK_BYTES) {
    throw new Error("File thời khóa biểu vượt quá giới hạn 25 MB.");
  }
  if (signal?.aborted) {
    throw abortError();
  }

  const bytes = new Uint8Array(await file.arrayBuffer());
  if (bytes.byteLength > MAX_WORKBOOK_BYTES) {
    throw new Error("File thời khóa biểu vượt quá giới hạn 25 MB.");
  }
  const chunkSize = 0x8000;
  let binary = "";
  for (let offset = 0; offset < bytes.length; offset += chunkSize) {
    if (signal?.aborted) {
      throw abortError();
    }
    binary += String.fromCharCode(...bytes.subarray(offset, offset + chunkSize));
  }
  return { file_name: file.name, bytes_base64: btoa(binary) };
}

function abortError(): DOMException {
  return new DOMException("The request was cancelled.", "AbortError");
}

async function postDesktop<T>(
  method: string,
  payload: unknown,
  signal?: AbortSignal,
  validateResult?: ResultValidator<T>,
  timeoutMilliseconds = DEFAULT_COMMAND_TIMEOUT_MS,
): Promise<T> {
  const webView = getDesktopWebView();
  if (!webView) {
    throw new Error("Desktop bridge is unavailable.");
  }
  if (signal?.aborted) {
    throw abortError();
  }
  const desktopWebView: DesktopWebView = webView;

  const id = crypto.randomUUID();
  return new Promise<T>((resolve, reject) => {
    let settled = false;

    function cleanup() {
      window.clearTimeout(timeout);
      desktopWebView.removeEventListener("message", onMessage);
      signal?.removeEventListener("abort", onAbort);
    }

    function sendCancellation() {
      if (settled) {
        return;
      }

      desktopWebView.postMessage({
        protocol_version: DESKTOP_BRIDGE_PROTOCOL_VERSION,
        id: crypto.randomUUID(),
        method: "cancel_command",
        payload: { target_id: id },
      });
    }

    const timeout = window.setTimeout(() => {
      cleanup();
      sendCancellation();
      settled = true;
      reject(new Error("Desktop bridge timed out."));
    }, timeoutMilliseconds);

    function onMessage(event: MessageEvent<unknown>) {
      const message = parseDesktopBridgeMessage(event.data);
      if (!message || message.id !== id) {
        return;
      }

      if (!isDesktopBridgeResponse(message)) {
        cleanup();
        settled = true;
        reject(new Error("Desktop bridge returned an invalid response."));
        return;
      }

      const response = message;
      if (response.protocol_version !== DESKTOP_BRIDGE_PROTOCOL_VERSION) {
        cleanup();
        settled = true;
        reject(new Error("Desktop bridge protocol version is incompatible."));
        return;
      }

      cleanup();
      settled = true;
      if (!response.ok) {
        reject(new Error(response.error || "Desktop bridge request failed."));
        return;
      }
      if (validateResult && !validateResult(response.result)) {
        reject(new Error("Desktop bridge returned an invalid result."));
        return;
      }
      resolve(response.result as T);
    }

    function onAbort() {
      cleanup();
      sendCancellation();
      settled = true;
      reject(abortError());
    }

    desktopWebView.addEventListener("message", onMessage);
    signal?.addEventListener("abort", onAbort, { once: true });
    desktopWebView.postMessage({
      protocol_version: DESKTOP_BRIDGE_PROTOCOL_VERSION,
      id,
      method,
      payload,
    });
  });
}

export async function validateWorkbook(
  file: File,
  signal?: AbortSignal,
): Promise<ValidateExistingResponse> {
  if (!getDesktopWebView()) {
    throw new Error("Không thể kết nối với ứng dụng máy tính.");
  }

  return postDesktop<ValidateExistingResponse>("validate_workbook", {
    workbook: await workbookPayload(file, signal),
  }, signal, isValidateExistingResponse);
}

export async function solveWorkbook(
  file: File,
  desiredAssignments: DesiredAssignmentPayload[],
  timeoutSeconds: number,
  signal?: AbortSignal,
): Promise<RescheduleResponse> {
  if (!getDesktopWebView()) {
    throw new Error("Không thể kết nối với ứng dụng máy tính.");
  }

  const workbook = await workbookPayload(file, signal);
  if (signal?.aborted) {
    throw abortError();
  }
  return postDesktop<RescheduleResponse>("solve_workbook", {
    workbook,
    desired_assignments: desiredAssignments,
    timeout_seconds: timeoutSeconds,
  }, signal, isRescheduleResponse, timeoutSeconds * 1_000 + SOLVE_OVERHEAD_TIMEOUT_MS);
}

export type UnsatArtifactExportResponse = {
  exported: boolean;
  file_name: string | null;
  cnf_sha256: string | null;
  variable_count: number | null;
  clause_count: number | null;
};

export async function exportUnsatArtifact(
  file: File,
  desiredAssignments: DesiredAssignmentPayload[],
  verificationToken: string,
  signal?: AbortSignal,
): Promise<UnsatArtifactExportResponse> {
  if (!getDesktopWebView()) {
    throw new Error("Formal UNSAT export is available in the desktop app.");
  }

  return postDesktop<UnsatArtifactExportResponse>("export_unsat_artifact", {
    workbook: await workbookPayload(file, signal),
    desired_assignments: desiredAssignments,
    verification_token: verificationToken,
  }, signal, isUnsatArtifactExportResponse);
}

function isDesktopBridgeResponse(value: Record<string, unknown>): value is DesktopBridgeResponse {
  return typeof value.protocol_version === "number"
    && typeof value.id === "string"
    && typeof value.ok === "boolean"
    && (value.error === undefined || value.error === null || typeof value.error === "string");
}

function parseDesktopBridgeMessage(value: unknown): Record<string, unknown> | null {
  if (isRecord(value)) {
    return value;
  }
  if (typeof value !== "string") {
    return null;
  }

  try {
    const parsed: unknown = JSON.parse(value);
    return isRecord(parsed) ? parsed : null;
  } catch {
    return null;
  }
}

function isValidateExistingResponse(value: unknown): value is ValidateExistingResponse {
  return isBaseTimetableResponse(value)
    && value.mode === "validate_existing"
    && isParseSummary(value.parse_summary)
    && isPrototypeCatalog(value.prototype_catalog)
    && isValidationSummary(value.existing_schedule_validation);
}

function isBaseTimetableResponse(value: unknown): value is Record<string, unknown> {
  return isRecord(value)
    && typeof value.workbook_path === "string"
    && typeof value.department === "string"
    && isParseSummary(value.parse_summary)
    && isPrototypeCatalog(value.prototype_catalog)
    && isValidationSummary(value.existing_schedule_validation);
}

function isRescheduleResponse(value: unknown): value is RescheduleResponse {
  if (!isBaseTimetableResponse(value)) {
    return false;
  }

  const response = value;
  const solutions = response.solutions;
  const solver = response.solver;
  const solutionIndices = Array.isArray(solutions)
    ? solutions.map((solution) => isRecord(solution) ? solution.solution_index : -1)
    : [];
  const hasUniqueSolutionIndices = new Set(solutionIndices).size === solutionIndices.length
    && solutionIndices.every((index, position) => index === position + 1);

  return response.mode === "reschedule"
    && Array.isArray(response.desired_assignments)
    && response.desired_assignments.every(isDesiredAssignmentSummary)
    && Array.isArray(solutions)
    && solutions.every(isSelectedSolution)
    && hasUniqueSolutionIndices
    && isSolverSummary(solver)
    && solver.solution_count === solutions.length
    && (solver.satisfiability !== "SAT" || solutions.length > 0)
    && (solver.satisfiability !== "UNSAT" || solutions.length === 0)
    && (response.solved_schedule_validation === null || isValidationSummary(response.solved_schedule_validation));
}

function isUnsatArtifactExportResponse(value: unknown): value is UnsatArtifactExportResponse {
  return isRecord(value) && typeof value.exported === "boolean";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}

function isParseSummary(value: unknown): boolean {
  if (!isRecord(value)) {
    return false;
  }

  const numericFieldsValid = [
      "sessions",
      "schedulable_sessions",
      "online_sessions",
      "other_department_sessions",
      "lecturer_blocks",
      "anchor_count",
      "requested_assignments",
      "rooms",
      "skipped_rows",
      "fatal_warning_count",
    ].every((key) => typeof value[key] === "number" && Number.isInteger(value[key]) && value[key] >= 0);

  return numericFieldsValid && isStringArray(value.warnings);
}

function isPrototypeCatalog(value: unknown): boolean {
  return isRecord(value)
    && Array.isArray(value.anchors)
    && value.anchors.every((anchor) => isRecord(anchor)
      && typeof anchor.course_code === "string"
      && typeof anchor.course_name === "string"
      && typeof anchor.teaching_team_key === "string"
      && typeof anchor.teaching_team_label === "string"
      && typeof anchor.session_count === "number")
    && Array.isArray(value.room_cost_rules)
    && value.room_cost_rules.every((rule) => isRecord(rule)
      && typeof rule.from_zone === "string"
      && typeof rule.to_zone === "string"
      && typeof rule.cost === "number"
      && typeof rule.description === "string");
}

function isValidationSummary(value: unknown): boolean {
  return isRecord(value)
    && typeof value.is_valid === "boolean"
    && typeof value.is_complete === "boolean"
    && typeof value.violation_count === "number"
    && typeof value.missing_session_count === "number"
    && isStringArray(value.sample_violations);
}

function isSessionScheduleItem(value: unknown): boolean {
  return isRecord(value)
    && ["session_id", "course_code", "course_name", "lhp_code", "session_type", "timeslot_label", "room_code"].every(
      (key) => typeof value[key] === "string",
    )
    && isStringArray(value.lecturer_names)
    && isStringArray(value.cohort_codes)
    && typeof value.source_row === "number"
    && (value.day === null || typeof value.day === "number")
    && (value.period_code === null || typeof value.period_code === "string")
    && (value.period_atomic === null || isStringArray(value.period_atomic));
}

function isDesiredAssignmentSummary(value: unknown): boolean {
  return isRecord(value)
    && ["course_code", "teaching_team_key", "teaching_team_label", "course_name"]
      .every((key) => typeof value[key] === "string")
    && isStringArray(value.lhp_codes)
    && typeof value.session_count === "number"
    && Array.isArray(value.matched_sessions)
    && value.matched_sessions.every(isSessionScheduleItem)
    && Array.isArray(value.lhp_schedules)
    && value.lhp_schedules.every((schedule) => isRecord(schedule)
      && typeof schedule.lhp_code === "string"
      && typeof schedule.session_count === "number"
      && Array.isArray(schedule.matched_sessions)
      && schedule.matched_sessions.every(isSessionScheduleItem));
}

function isSelectedSolution(value: unknown): boolean {
  return isRecord(value)
    && typeof value.solution_index === "number"
    && typeof value.movement_cost === "number"
    && Array.isArray(value.desired_assignments)
    && value.desired_assignments.every(isDesiredAssignmentSummary);
}

function isSolverSummary(value: unknown): value is SolverSummaryShape {
  if (!isRecord(value)) {
    return false;
  }

  const status = value.status;
  const satisfiability = value.satisfiability;
  const solutionCount = value.solution_count;
  const assignmentCount = value.assignment_count;
  const validShape = typeof value.backend === "string"
    && ["feasible", "infeasible", "timeout"].includes(status as string)
    && ["SAT", "UNSAT", "UNKNOWN"].includes(satisfiability as string)
    && typeof value.solver_info === "string"
    && typeof value.solve_time_ms === "number"
    && Number.isFinite(value.solve_time_ms)
    && value.solve_time_ms >= 0
    && (value.objective_value === null || typeof value.objective_value === "number")
    && typeof assignmentCount === "number" && Number.isInteger(assignmentCount) && assignmentCount >= 0
    && typeof solutionCount === "number" && Number.isInteger(solutionCount) && solutionCount >= 0
    && isStringArray(value.explanation)
    && (value.formal_verification_token === null || typeof value.formal_verification_token === "string");

  if (!validShape) {
    return false;
  }

  return (status === "feasible" && satisfiability === "SAT" && solutionCount > 0)
    || (status === "infeasible" && satisfiability === "UNSAT" && solutionCount === 0)
    || (status === "timeout" && satisfiability === "UNKNOWN" && solutionCount === 0);
}

export async function setDesktopTheme(preference: ThemePreference): Promise<void> {
  if (!getDesktopWebView()) {
    return;
  }

  await postDesktop("set_theme", { preference });
}

export async function notifyDesktopReady(): Promise<void> {
  if (!getDesktopWebView()) {
    return;
  }

  await postDesktop("desktop_ready", {});
}
