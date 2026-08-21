import type {
  DesiredAssignmentPayload,
  DesiredAnchorSummary,
  RescheduleResponse,
  SaveTimetableResponse,
  SelectedSolutionItem,
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
export const MAX_DESKTOP_MESSAGE_CHARACTERS = 40 * 1024 * 1024;
// The bridge transfers base64 JSON, which temporarily holds several copies of a file.
// Keep the accepted desktop document size below the renderer memory budget.
const MAX_WORKBOOK_BYTES = 25 * 1024 * 1024;
const DEFAULT_COMMAND_TIMEOUT_MS = 60_000;
const SOLVE_OVERHEAD_TIMEOUT_MS = 60_000;
const VALID_SESSION_TYPES = new Set(["LT", "TH", "LT+TH", "TL+TH", "BT", "LT+BT", "ONL"]);
const VALID_PERIOD_CODES = new Set(["1", "2", "3", "4", "1-2", "2-3", "3-4", "Sáng", "Chiều"]);
const VALID_ATOMIC_PERIODS = new Set(["1", "2", "3", "4"]);
const EXPECTED_PERIOD_ATOMICS = new Map<string, readonly string[]>([
  ["1", ["1"]],
  ["2", ["2"]],
  ["3", ["3"]],
  ["4", ["4"]],
  ["1-2", ["1", "2"]],
  ["2-3", ["2", "3"]],
  ["3-4", ["3", "4"]],
  ["Sáng", ["1", "2"]],
  ["Chiều", ["3", "4"]],
]);
const MOVEMENT_ZONE_BY_BUILDING = new Map<string, string>([
  ["A", "GD4"],
  ["B", "GD4"],
  ["T", "GD3"],
  ["GĐ3", "GD3"],
  ["G2", "GD2"],
  ["G3", "GD2"],
  ["E3", "GD2"],
  ["E5", "GD2"],
]);

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

export function isTimetableTooLargeError(error: unknown): boolean {
  const raw = error instanceof Error ? error.message : "";
  return /too large|vượt quá giới hạn 25\s*(?:MB|MiB)/i.test(raw);
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
  const requestMessage = {
    protocol_version: DESKTOP_BRIDGE_PROTOCOL_VERSION,
    id,
    method,
    payload,
  };
  const serializedRequest = JSON.stringify(requestMessage);
  if (serializedRequest.length > MAX_DESKTOP_MESSAGE_CHARACTERS) {
    throw new Error("Desktop bridge request is too large.");
  }

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
    desktopWebView.postMessage(requestMessage);
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
    && (response.desired_assignments.length === 0
      || hasUniqueRenderedSessionIds(response.desired_assignments as DesiredAnchorSummary[]))
    && (response.desired_assignments.length === 0
      || hasUniqueAnchorIdentities(response.desired_assignments as DesiredAnchorSummary[]))
     && isSolverSummary(solver)
     && solver.solution_count === solutions.length
     && solutions.length <= MAX_TIMETABLE_SOLUTIONS
    && (solver.satisfiability !== "SAT" || response.desired_assignments.length > 0)
    && (solver.satisfiability !== "SAT" || solutions.length > 0)
    && (solver.satisfiability !== "UNSAT" || solutions.length === 0)
    && hasRootAssignmentsMatchingEverySolution(
      response.desired_assignments as DesiredAnchorSummary[],
      solutions as RescheduleResponse["solutions"],
    )
    && hasUniqueSolutionPlacements(solutions as RescheduleResponse["solutions"])
    && hasMovementCostConsistency(solutions as RescheduleResponse["solutions"])
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
    && typeof value.course_code === "string"
    && typeof value.course_name === "string"
    && typeof value.lhp_code === "string"
    && typeof value.session_type === "string"
    && VALID_SESSION_TYPES.has(value.session_type)
    && typeof value.timeslot_label === "string"
    && typeof value.room_code === "string"
    && isNonEmptyString(value.course_code)
    && isNonEmptyString(value.lhp_code)
    && isNonEmptyString(value.session_id)
    && isNonEmptyString(value.room_code)
    && isStringArray(value.lecturer_names)
    && isStringArray(value.cohort_codes)
    && isInteger(value.source_row)
    && (value.day === null || isInteger(value.day))
    && (value.period_code === null || typeof value.period_code === "string")
    && (value.period_atomic === null || isStringArray(value.period_atomic))
    && isValidSessionTiming(value);
}

function isValidSessionTiming(value: Record<string, unknown>): boolean {
  const { day, period_code: periodCode, period_atomic: periodAtomic } = value;
  const isOnline = value.session_type === "ONL";
  if (value.room_code === "-" || (isOnline ? value.room_code !== "ONL" : value.room_code === "ONL")) {
    return false;
  }

  if (day === null && periodCode === null && periodAtomic === null) {
    return isOnline;
  }

  if (!isInteger(day)
    || day < 2
    || day > 7
    || typeof periodCode !== "string"
    || !VALID_PERIOD_CODES.has(periodCode)
    || !isStringArray(periodAtomic)
    || periodAtomic.length === 0
    || new Set(periodAtomic).size !== periodAtomic.length
    || !periodAtomic.every((period) => VALID_ATOMIC_PERIODS.has(period))) {
    return false;
  }

  const expectedAtomicPeriods = EXPECTED_PERIOD_ATOMICS.get(periodCode);
  return expectedAtomicPeriods !== undefined
    && periodAtomic.length === expectedAtomicPeriods.length
    && periodAtomic.every((period, index) => period === expectedAtomicPeriods[index]);
}

function isDesiredAssignmentSummary(value: unknown): boolean {
  if (!isRecord(value)
    || !hasOnlyKeys(value, [
      "course_code",
      "teaching_team_key",
      "teaching_team_label",
      "course_name",
      "lhp_codes",
      "session_count",
      "matched_sessions",
      "lhp_schedules",
    ])
    || !["course_code", "teaching_team_key", "teaching_team_label", "course_name"]
      .every((key) => isNonEmptyString(value[key]))
    || !isStringArray(value.lhp_codes)
    || !isNonNegativeInteger(value.session_count)
    || !Array.isArray(value.matched_sessions)
    || value.matched_sessions.length === 0
    || !value.matched_sessions.every(isSessionScheduleItem)
    || !Array.isArray(value.lhp_schedules)
    || value.lhp_schedules.length === 0
    || !value.lhp_schedules.every((schedule) => isRecord(schedule)
      && hasOnlyKeys(schedule, ["lhp_code", "session_count", "matched_sessions"])
      && isNonEmptyString(schedule.lhp_code)
      && isNonNegativeInteger(schedule.session_count)
      && Array.isArray(schedule.matched_sessions)
      && schedule.matched_sessions.length > 0
      && schedule.session_count === schedule.matched_sessions.length
      && schedule.matched_sessions.every(isSessionScheduleItem))) {
    return false;
  }

  return isSemanticallyConsistentDesiredAssignment(value as DesiredAnchorSummary);
}

function isSemanticallyConsistentDesiredAssignment(anchor: DesiredAnchorSummary): boolean {
  const lhpCodes = anchor.lhp_codes;
  const schedules = anchor.lhp_schedules;
  const renderedSessions = schedules.flatMap((schedule) => schedule.matched_sessions);
  const scheduleCodes = schedules.map((schedule) => schedule.lhp_code);

  return lhpCodes.length > 0
    && lhpCodes.every(isNonEmptyString)
    && new Set(lhpCodes).size === lhpCodes.length
    && sameStringSet(lhpCodes, scheduleCodes)
    && anchor.session_count === anchor.matched_sessions.length
    && anchor.session_count === renderedSessions.length
    && anchor.matched_sessions.every((session) => session.course_code === anchor.course_code)
    && schedules.every((schedule) => schedule.matched_sessions.every((session) =>
      session.course_code === anchor.course_code && session.lhp_code === schedule.lhp_code))
    && areEquivalentSessionCollections(anchor.matched_sessions, renderedSessions);
}

function sameStringSet(left: string[], right: string[]): boolean {
  const leftSet = new Set(left);
  const rightSet = new Set(right);
  return leftSet.size === left.length
    && rightSet.size === right.length
    && leftSet.size === rightSet.size
    && left.every((value) => rightSet.has(value));
}

type SessionScheduleItem = DesiredAnchorSummary["matched_sessions"][number];

function areEquivalentSessionCollections(
  left: SessionScheduleItem[],
  right: SessionScheduleItem[],
): boolean {
  if (left.length !== right.length
    || new Set(left.map((session) => session.session_id)).size !== left.length
    || new Set(right.map((session) => session.session_id)).size !== right.length) {
    return false;
  }

  const rightById = new Map(right.map((session) => [session.session_id, session]));
  return left.every((session) => {
    const counterpart = rightById.get(session.session_id);
    return counterpart !== undefined && sessionSemanticKey(session) === sessionSemanticKey(counterpart);
  });
}

function sessionSemanticKey(session: SessionScheduleItem): string {
  return JSON.stringify([
    session.session_id,
    session.course_code,
    session.course_name,
    session.lhp_code,
    session.session_type,
    session.lecturer_names,
    session.cohort_codes,
    session.timeslot_label,
    session.room_code,
    session.source_row,
    session.day,
    session.period_code,
    session.period_atomic,
  ]);
}

function hasRootAssignmentsMatchingEverySolution(
  rootAssignments: DesiredAnchorSummary[],
  solutions: RescheduleResponse["solutions"],
): boolean {
  if (solutions.length === 0) {
    return rootAssignments.length === 0;
  }

  return areEquivalentDesiredAssignments(rootAssignments, solutions[0].desired_assignments)
    && solutions.every((solution) =>
      areEquivalentDesiredAnchorIdentities(rootAssignments, solution.desired_assignments));
}

function areEquivalentDesiredAssignments(
  left: DesiredAnchorSummary[],
  right: DesiredAnchorSummary[],
): boolean {
  return left.length === right.length
    && left.every((anchor, index) => areEquivalentDesiredAssignment(anchor, right[index]));
}

function areEquivalentDesiredAnchorIdentities(
  left: DesiredAnchorSummary[],
  right: DesiredAnchorSummary[],
): boolean {
  return left.length === right.length
    && left.every((anchor, index) => {
      const counterpart = right[index];
      return anchor.course_code === counterpart.course_code
        && anchor.teaching_team_key === counterpart.teaching_team_key
        && anchor.teaching_team_label === counterpart.teaching_team_label
        && anchor.course_name === counterpart.course_name;
    });
}

function areEquivalentDesiredAssignment(
  left: DesiredAnchorSummary,
  right: DesiredAnchorSummary,
): boolean {
  return left.course_code === right.course_code
    && left.teaching_team_key === right.teaching_team_key
    && left.teaching_team_label === right.teaching_team_label
    && left.course_name === right.course_name
    && left.session_count === right.session_count
    && left.lhp_codes.length === right.lhp_codes.length
    && left.lhp_codes.every((code, index) => code === right.lhp_codes[index])
    && areEquivalentSessionCollections(left.matched_sessions, right.matched_sessions)
    && left.lhp_schedules.length === right.lhp_schedules.length
    && left.lhp_schedules.every((schedule, index) => {
      const counterpart = right.lhp_schedules[index];
      return schedule.lhp_code === counterpart.lhp_code
        && schedule.session_count === counterpart.session_count
        && areEquivalentSessionCollections(schedule.matched_sessions, counterpart.matched_sessions);
    });
}

function hasUniqueRenderedSessionIds(desiredAssignments: DesiredAnchorSummary[]): boolean {
  const sessionIds = desiredAssignments.flatMap((anchor) =>
    anchor.lhp_schedules.flatMap((schedule) => schedule.matched_sessions.map((session) => session.session_id)));
  return new Set(sessionIds).size === sessionIds.length;
}

function hasUniqueAnchorIdentities(desiredAssignments: DesiredAnchorSummary[]): boolean {
  const keys = desiredAssignments.map((anchor) => JSON.stringify([
    anchor.course_code,
    anchor.teaching_team_key,
  ]));
  return new Set(keys).size === keys.length;
}

function hasUniqueSolutionPlacements(solutions: SelectedSolutionItem[]): boolean {
  for (let leftIndex = 0; leftIndex < solutions.length; leftIndex += 1) {
    for (let rightIndex = leftIndex + 1; rightIndex < solutions.length; rightIndex += 1) {
      if (areEquivalentDesiredAssignments(
        solutions[leftIndex].desired_assignments,
        solutions[rightIndex].desired_assignments,
      )) {
        return false;
      }
    }
  }

  return true;
}

function hasMovementCostConsistency(solutions: SelectedSolutionItem[]): boolean {
  let previousCost = -1;
  for (const solution of solutions) {
    const recomputedCost = calculateMovementCost(solution);
    if (solution.movement_cost !== recomputedCost || recomputedCost < previousCost) {
      return false;
    }
    previousCost = recomputedCost;
  }

  return true;
}

type CostSession = {
  session: SessionScheduleItem;
  startPeriod: number;
  endPeriod: number;
};

function calculateMovementCost(solution: SelectedSolutionItem): number {
  const sessionsByDay = new Map<number, CostSession[]>();
  for (const session of solution.desired_assignments.flatMap((anchor) =>
    anchor.lhp_schedules.flatMap((schedule) => schedule.matched_sessions))) {
    if (session.day === null || session.period_atomic === null) {
      continue;
    }

    const periods = session.period_atomic;
    const daySessions = sessionsByDay.get(session.day) ?? [];
    daySessions.push({
      session,
      startPeriod: Number(periods[0]),
      endPeriod: Number(periods[periods.length - 1]),
    });
    sessionsByDay.set(session.day, daySessions);
  }

  let totalCost = 0;
  for (const daySessions of sessionsByDay.values()) {
    const ordered = [...daySessions].sort((left, right) =>
      left.startPeriod - right.startPeriod
      || left.endPeriod - right.endPeriod
      || left.session.source_row - right.session.source_row
      || compareOrdinal(left.session.session_id, right.session.session_id));
    for (let index = 1; index < ordered.length; index += 1) {
      const previous = ordered[index - 1];
      const current = ordered[index];
      if (previous.endPeriod + 1 === current.startPeriod) {
        totalCost += transitionCost(previous.session, current.session);
      }
    }
  }

  return totalCost;
}

function transitionCost(
  from: SessionScheduleItem,
  to: SessionScheduleItem,
): number {
  if (from.session_type === "ONL"
    || to.session_type === "ONL"
    || from.room_code === "ONL"
    || to.room_code === "ONL") {
    return 0;
  }

  if (from.room_code === to.room_code) {
    return 0;
  }

  const fromBuilding = roomBuilding(from.room_code);
  const toBuilding = roomBuilding(to.room_code);
  if (fromBuilding === toBuilding) {
    return 1;
  }

  return movementZone(fromBuilding) === movementZone(toBuilding) ? 2 : 3;
}

function roomBuilding(roomCode: string): string {
  const separator = roomCode.lastIndexOf("-");
  return separator < 0 ? roomCode : roomCode.slice(separator + 1);
}

function compareOrdinal(left: string, right: string): number {
  const length = Math.min(left.length, right.length);
  for (let index = 0; index < length; index += 1) {
    const difference = left.charCodeAt(index) - right.charCodeAt(index);
    if (difference !== 0) {
      return difference;
    }
  }

  return left.length - right.length;
}

function movementZone(building: string): string {
  if (building.includes("ĐHKHTN")) {
    return "ĐHKHTN";
  }

  return MOVEMENT_ZONE_BY_BUILDING.get(building) ?? building;
}

function isSelectedSolution(value: unknown): boolean {
  return isRecord(value)
    && hasOnlyKeys(value, ["solution_index", "movement_cost", "desired_assignments"])
    && isInteger(value.solution_index)
    && isNonNegativeInteger(value.movement_cost)
    && Array.isArray(value.desired_assignments)
    && value.desired_assignments.length > 0
    && value.desired_assignments.every(isDesiredAssignmentSummary)
    && hasUniqueRenderedSessionIds(value.desired_assignments as DesiredAnchorSummary[])
    && hasUniqueAnchorIdentities(value.desired_assignments as DesiredAnchorSummary[]);
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
    || (status === "timeout"
      && satisfiability === "UNKNOWN"
      && solutionCount >= 0
      && solutionCount <= MAX_TIMETABLE_SOLUTIONS);
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
