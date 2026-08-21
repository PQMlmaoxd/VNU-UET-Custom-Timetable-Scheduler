import type {
  DesiredAssignmentPayload,
  RescheduleResponse,
  SaveTimetableResponse,
  ValidateExistingResponse,
} from "./types";
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
export const MAX_TIMETABLE_SOLUTIONS = 5;
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
  timeoutMilliseconds: number | null = DEFAULT_COMMAND_TIMEOUT_MS,
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
      if (timeout !== null) {
        window.clearTimeout(timeout);
      }
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

    const timeout = timeoutMilliseconds === null
      ? null
      : window.setTimeout(() => {
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

export async function saveTimetable(
  timetable: RescheduleResponse,
  sourceFileName: string,
): Promise<SaveTimetableResponse> {
  if (!getDesktopWebView()) {
    throw new Error("Lưu thời khóa biểu JSON chỉ khả dụng trong ứng dụng Desktop.");
  }

  return postDesktop<SaveTimetableResponse>("save_timetable", {
    timetable,
    source_file_name: sourceFileName,
  }, undefined, isSaveTimetableResponse, null);
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
    && hasOnlyKeys(value, [
      "mode",
      "workbook_path",
      "department",
      "parse_summary",
      "prototype_catalog",
      "existing_schedule_validation",
    ])
    && value.mode === "validate_existing"
    && isParseSummary(value.parse_summary)
    && isPrototypeCatalog(value.prototype_catalog)
    && isValidationSummary(value.existing_schedule_validation);
}

function isBaseTimetableResponse(value: unknown): value is Record<string, unknown> {
  return isRecord(value)
    && isNonEmptyString(value.workbook_path)
    && isNonEmptyString(value.department)
    && isParseSummary(value.parse_summary)
    && isPrototypeCatalog(value.prototype_catalog)
    && isValidationSummary(value.existing_schedule_validation);
}

export function isRescheduleResponse(value: unknown): value is RescheduleResponse {
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

  return hasOnlyKeys(response, [
    "mode",
    "workbook_path",
    "department",
    "parse_summary",
    "prototype_catalog",
    "desired_assignments",
    "solutions",
    "existing_schedule_validation",
    "solver",
    "solved_schedule_validation",
  ])
    && response.mode === "reschedule"
    && Array.isArray(response.desired_assignments)
    && response.desired_assignments.every(isDesiredAssignmentSummary)
    && Array.isArray(solutions)
    && solutions.every(isSelectedSolution)
    && hasUniqueSolutionIndices
    && isSolverSummary(solver)
    && solver.solution_count === solutions.length
    && solutions.length <= MAX_TIMETABLE_SOLUTIONS
    && (solver.satisfiability !== "SAT" || response.desired_assignments.length > 0)
    && (solver.satisfiability !== "SAT" || solutions.length > 0)
    && (solver.satisfiability !== "UNSAT" || solutions.length === 0)
    && (response.solved_schedule_validation === null || isValidationSummary(response.solved_schedule_validation));
}

function isUnsatArtifactExportResponse(value: unknown): value is UnsatArtifactExportResponse {
  return isRecord(value) && typeof value.exported === "boolean";
}

function isSaveTimetableResponse(value: unknown): value is SaveTimetableResponse {
  return isRecord(value)
    && typeof value.saved === "boolean"
    && (value.file_name === null || typeof value.file_name === "string");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === "string" && value.trim().length > 0;
}

function hasOnlyKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  return Object.keys(value).length === keys.length
    && keys.every((key) => Object.prototype.hasOwnProperty.call(value, key));
}

function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === "string");
}

function isInteger(value: unknown): value is number {
  return typeof value === "number"
    && Number.isInteger(value)
    && value >= -2147483648
    && value <= 2147483647;
}

function isNonNegativeInteger(value: unknown): value is number {
  return isInteger(value) && value >= 0;
}

function isParseSummary(value: unknown): boolean {
  if (!isRecord(value) || !hasOnlyKeys(value, [
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
    "partial_import",
    "quarantined_lhp_count",
    "quarantined_session_count",
    "warnings",
    "fatal_warnings",
    "quarantined_offerings",
  ])) {
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
    "quarantined_lhp_count",
    "quarantined_session_count",
  ].every((key) => isNonNegativeInteger(value[key]));

  return numericFieldsValid
    && typeof value.partial_import === "boolean"
    && isStringArray(value.warnings)
    && isStringArray(value.fatal_warnings)
    && Array.isArray(value.quarantined_offerings)
    && value.quarantined_offerings.every((offering) => isRecord(offering)
      && hasOnlyKeys(offering, [
        "course_code",
        "lhp_code",
        "reason_code",
        "source_locations",
        "quarantined_row_count",
        "excluded_session_count",
      ])
      && typeof offering.course_code === "string"
      && typeof offering.lhp_code === "string"
      && typeof offering.reason_code === "string"
      && isStringArray(offering.source_locations)
      && isNonNegativeInteger(offering.quarantined_row_count)
      && isNonNegativeInteger(offering.excluded_session_count));
}

function isPrototypeCatalog(value: unknown): boolean {
  return isRecord(value)
    && hasOnlyKeys(value, ["anchors", "room_cost_rules"])
    && Array.isArray(value.anchors)
    && value.anchors.every((anchor) => isRecord(anchor)
      && hasOnlyKeys(anchor, [
        "course_code",
        "course_name",
        "teaching_team_key",
        "teaching_team_label",
        "session_count",
      ])
      && typeof anchor.course_code === "string"
      && typeof anchor.course_name === "string"
      && typeof anchor.teaching_team_key === "string"
      && typeof anchor.teaching_team_label === "string"
      && isNonNegativeInteger(anchor.session_count))
    && Array.isArray(value.room_cost_rules)
    && value.room_cost_rules.every((rule) => isRecord(rule)
      && hasOnlyKeys(rule, ["from_zone", "to_zone", "cost", "description"])
      && typeof rule.from_zone === "string"
      && typeof rule.to_zone === "string"
      && isInteger(rule.cost)
      && typeof rule.description === "string");
}

function isValidationSummary(value: unknown): boolean {
  return isRecord(value)
    && hasOnlyKeys(value, [
      "is_valid",
      "is_complete",
      "violation_count",
      "missing_session_count",
      "sample_violations",
    ])
    && typeof value.is_valid === "boolean"
    && typeof value.is_complete === "boolean"
    && isNonNegativeInteger(value.violation_count)
    && isNonNegativeInteger(value.missing_session_count)
    && isStringArray(value.sample_violations);
}

function isSessionScheduleItem(value: unknown): boolean {
  return isRecord(value)
    && hasOnlyKeys(value, [
      "session_id",
      "course_code",
      "course_name",
      "lhp_code",
      "session_type",
      "lecturer_names",
      "cohort_codes",
      "timeslot_label",
      "room_code",
      "source_row",
      "day",
      "period_code",
      "period_atomic",
    ])
    && ["session_id", "course_code", "course_name", "lhp_code", "session_type", "timeslot_label", "room_code"].every(
      (key) => typeof value[key] === "string",
    )
    && isStringArray(value.lecturer_names)
    && isStringArray(value.cohort_codes)
    && isInteger(value.source_row)
    && (value.day === null || isInteger(value.day))
    && (value.period_code === null || typeof value.period_code === "string")
    && (value.period_atomic === null || isStringArray(value.period_atomic));
}

function isDesiredAssignmentSummary(value: unknown): boolean {
  return isRecord(value)
    && hasOnlyKeys(value, [
      "course_code",
      "teaching_team_key",
      "teaching_team_label",
      "course_name",
      "lhp_codes",
      "session_count",
      "matched_sessions",
      "lhp_schedules",
    ])
    && ["course_code", "teaching_team_key", "teaching_team_label", "course_name"]
      .every((key) => typeof value[key] === "string")
    && isStringArray(value.lhp_codes)
    && isNonNegativeInteger(value.session_count)
    && Array.isArray(value.matched_sessions)
    && value.matched_sessions.length > 0
    && value.matched_sessions.every(isSessionScheduleItem)
    && Array.isArray(value.lhp_schedules)
    && value.lhp_schedules.length > 0
    && value.lhp_schedules.every((schedule) => isRecord(schedule)
      && hasOnlyKeys(schedule, ["lhp_code", "session_count", "matched_sessions"])
      && typeof schedule.lhp_code === "string"
      && isNonNegativeInteger(schedule.session_count)
      && Array.isArray(schedule.matched_sessions)
      && schedule.matched_sessions.length > 0
      && schedule.matched_sessions.every(isSessionScheduleItem));
}

function isSelectedSolution(value: unknown): boolean {
  return isRecord(value)
    && hasOnlyKeys(value, ["solution_index", "movement_cost", "desired_assignments"])
    && isInteger(value.solution_index)
    && isInteger(value.movement_cost)
    && Array.isArray(value.desired_assignments)
    && value.desired_assignments.length > 0
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
    && hasOnlyKeys(value, [
      "backend",
      "status",
      "satisfiability",
      "solve_time_ms",
      "objective_value",
      "assignment_count",
      "solution_count",
      "solver_info",
      "explanation",
      "formal_verification_token",
    ])
    && ["feasible", "infeasible", "timeout"].includes(status as string)
    && ["SAT", "UNSAT", "UNKNOWN"].includes(satisfiability as string)
    && typeof value.solver_info === "string"
    && isNonNegativeInteger(value.solve_time_ms)
    && (value.objective_value === null || (typeof value.objective_value === "number" && Number.isFinite(value.objective_value)))
    && isNonNegativeInteger(assignmentCount)
    && isNonNegativeInteger(solutionCount)
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
