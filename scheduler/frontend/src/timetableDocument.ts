import { isRescheduleResponse, isWellFormedString } from "./api";
import type { TimetableDocument } from "./types";

export const TIMETABLE_DOCUMENT_SCHEMA = "vnu-uet-timetable";
export const TIMETABLE_DOCUMENT_VERSION = 1;
export const MAX_TIMETABLE_DOCUMENT_BYTES = 25 * 1024 * 1024;

export type TimetableDocumentErrorCode =
  | "invalid-json"
  | "invalid-envelope"
  | "unsupported-version"
  | "newer-version"
  | "invalid-timetable"
  | "not-sat";

export type TimetableDocumentParseResult =
  | { ok: true; document: TimetableDocument }
  | { ok: false; code: TimetableDocumentErrorCode };

export function parseTimetableDocument(text: string): TimetableDocumentParseResult {
  const normalizedText = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  let parsed: unknown;
  try {
    parsed = JSON.parse(normalizedText);
  } catch {
    return { ok: false, code: "invalid-json" };
  }

  if (!isRecord(parsed) ||
      parsed.schema !== TIMETABLE_DOCUMENT_SCHEMA ||
      typeof parsed.schema_version !== "number" ||
      !Number.isInteger(parsed.schema_version) ||
      !isWellFormedString(parsed.saved_at_utc) ||
      !isWellFormedString(parsed.app_version) ||
      !isRecord(parsed.source) ||
      !isWellFormedString(parsed.source.file_name) ||
      !Object.prototype.hasOwnProperty.call(parsed, "timetable")) {
    return { ok: false, code: "invalid-envelope" };
  }

  if (parsed.schema_version > TIMETABLE_DOCUMENT_VERSION) {
    return { ok: false, code: "newer-version" };
  }
  if (parsed.schema_version !== TIMETABLE_DOCUMENT_VERSION) {
    return { ok: false, code: "unsupported-version" };
  }

  if (!isRescheduleResponse(parsed.timetable)) {
    return { ok: false, code: "invalid-timetable" };
  }
  if (parsed.timetable.solver.satisfiability !== "SAT" || parsed.timetable.solutions.length === 0) {
    return { ok: false, code: "not-sat" };
  }

  return {
    ok: true,
    document: {
      schema: TIMETABLE_DOCUMENT_SCHEMA,
      schema_version: TIMETABLE_DOCUMENT_VERSION,
      saved_at_utc: parsed.saved_at_utc,
      app_version: parsed.app_version,
      source: { file_name: parsed.source.file_name },
      timetable: parsed.timetable,
    },
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null;
}
