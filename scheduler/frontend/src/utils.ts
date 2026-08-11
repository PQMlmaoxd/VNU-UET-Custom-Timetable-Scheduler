import type {
  AnchorCatalogItem,
  CourseCatalogItem,
  DesiredAnchorSummary,
  DesiredAssignmentPayload,
  LhpScheduleItem,
  SelectedSolutionItem,
  SelectionRow,
  SessionScheduleItem,
} from "./types";

export const TIMEOUT_MIN = 10;
export const TIMEOUT_MAX = 300;
export const TIMEOUT_DEFAULT = 180;

export const DAY_LABELS = new Map<number, string>([
  [2, "Thứ 2"],
  [3, "Thứ 3"],
  [4, "Thứ 4"],
  [5, "Thứ 5"],
  [6, "Thứ 6"],
  [7, "Thứ 7"],
]);

export const PERIOD_LABELS = new Map<string, { label: string; time: string }>([
  ["1", { label: "Ca 1", time: "07:00-09:40" }],
  ["2", { label: "Ca 2", time: "09:50-12:30" }],
  ["3", { label: "Ca 3", time: "13:30-16:10" }],
  ["4", { label: "Ca 4", time: "16:20-19:00" }],
]);

export const PERIODS = ["1", "2", "3", "4"] as const;
export const DAYS = [2, 3, 4, 5, 6, 7] as const;

export function normalizeText(value: string): string {
  return value
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/đ/g, "d")
    .replace(/Đ/g, "D")
    .toLowerCase()
    .trim();
}

export function courseKey(courseCode: string, courseName: string): string {
  return `${courseCode}::${courseName}`;
}

export function courseLabel(courseCode: string, courseName: string): string {
  return courseName ? `${courseCode} · ${courseName}` : courseCode;
}

export function buildCourseCatalog(anchors: AnchorCatalogItem[]): CourseCatalogItem[] {
  const grouped = new Map<string, CourseCatalogItem>();

  for (const anchor of anchors) {
    const key = courseKey(anchor.course_code, anchor.course_name);
    const current = grouped.get(key);
    if (current) {
      current.lecturers.push(anchor);
      current.searchText = normalizeText(
        `${current.course_code} ${current.course_name} ${current.lecturers
          .map((team) => team.teaching_team_label)
          .join(" ")}`,
      );
    } else {
      grouped.set(key, {
        key,
        course_code: anchor.course_code,
        course_name: anchor.course_name,
        lecturers: [anchor],
        searchText: normalizeText(`${anchor.course_code} ${anchor.course_name} ${anchor.teaching_team_label}`),
      });
    }
  }

  return Array.from(grouped.values())
    .map((course) => ({
      ...course,
      lecturers: [...course.lecturers].sort((left, right) =>
        left.teaching_team_label.localeCompare(right.teaching_team_label, "vi"),
      ),
    }))
    .sort((left, right) =>
      left.course_code.localeCompare(right.course_code, "vi") ||
      left.course_name.localeCompare(right.course_name, "vi"),
    );
}

export function matchingCourses(
  catalog: CourseCatalogItem[],
  query: string,
  selectedCourseKeys: Set<string>,
  limit = 8,
): CourseCatalogItem[] {
  const normalizedQuery = normalizeText(query);
  const available = catalog.filter((course) => !selectedCourseKeys.has(course.key));
  if (!normalizedQuery) {
    return available.slice(0, limit);
  }

  return available
    .map((course) => {
      const startsWithCode = normalizeText(course.course_code).startsWith(normalizedQuery) ? 0 : 1;
      const includes = course.searchText.includes(normalizedQuery) ? 0 : 2;
      return { course, score: startsWithCode + includes };
    })
    .filter(({ score }) => score < 3)
    .sort((left, right) => left.score - right.score)
    .map(({ course }) => course)
    .slice(0, limit);
}

export function selectedAssignments(rows: SelectionRow[]): DesiredAssignmentPayload[] {
  return rows
    .filter((row) => row.course_code && row.teaching_team_key && row.teaching_team_label)
    .map((row) => ({
      course_code: row.course_code,
      course_name: row.course_name,
      teaching_team_key: row.teaching_team_key,
      teaching_team_label: row.teaching_team_label,
    }));
}

export function hasIncompleteSelections(rows: SelectionRow[]): boolean {
  return rows.some((row) => !row.course_code || !row.teaching_team_key || !row.teaching_team_label);
}

export function clampTimeout(value: number): number {
  if (!Number.isFinite(value)) {
    return TIMEOUT_DEFAULT;
  }
  return Math.min(TIMEOUT_MAX, Math.max(TIMEOUT_MIN, Math.round(value)));
}

export function isXlsxFile(file: File | null): boolean {
  return Boolean(file && /\.xlsx$/i.test(file.name));
}

export function isPdfFile(file: File | null): boolean {
  return Boolean(file && /\.pdf$/i.test(file.name));
}

export function isSupportedTimetableFile(file: File | null, desktopApp: boolean): boolean {
  return isXlsxFile(file) || (desktopApp && isPdfFile(file));
}

export function normalizedLhpSchedules(anchor: DesiredAnchorSummary): LhpScheduleItem[] {
  if (anchor.lhp_schedules.length > 0) {
    return anchor.lhp_schedules;
  }

  const byLhp = new Map<string, SessionScheduleItem[]>();
  for (const session of anchor.matched_sessions) {
    const sessions = byLhp.get(session.lhp_code) ?? [];
    sessions.push(session);
    byLhp.set(session.lhp_code, sessions);
  }

  return Array.from(byLhp.entries())
    .map(([lhp_code, matched_sessions]) => ({
      lhp_code,
      session_count: matched_sessions.length,
      matched_sessions: [...matched_sessions].sort(sessionSortKey),
    }))
    .sort((left, right) => left.lhp_code.localeCompare(right.lhp_code, "vi"));
}

export function sessionsForSolution(solution: SelectedSolutionItem): SessionScheduleItem[] {
  return solution.desired_assignments.flatMap((anchor) =>
    normalizedLhpSchedules(anchor).flatMap((lhp) => lhp.matched_sessions),
  );
}

export function onlineSessionsForSolution(solution: SelectedSolutionItem): SessionScheduleItem[] {
  return sessionsForSolution(solution).filter((session) => session.session_type === "ONL");
}

export function physicalSessionsForSolution(solution: SelectedSolutionItem): SessionScheduleItem[] {
  return sessionsForSolution(solution)
    .filter((session) => session.session_type !== "ONL" && session.day !== null)
    .sort(sessionSortKey);
}

export function sessionSortKey(left: SessionScheduleItem, right: SessionScheduleItem): number {
  return sessionOrder(left) - sessionOrder(right) || left.course_code.localeCompare(right.course_code, "vi");
}

function sessionOrder(session: SessionScheduleItem): number {
  const day = session.day ?? 99;
  const firstPeriod = Number(session.period_atomic?.[0] ?? session.period_code ?? 99);
  return day * 10 + firstPeriod;
}

export function solutionStats(solution: SelectedSolutionItem): { lhpCount: number; sessionCount: number } {
  const lhpCodes = new Set<string>();
  let sessionCount = 0;

  for (const anchor of solution.desired_assignments) {
    for (const lhp of normalizedLhpSchedules(anchor)) {
      lhpCodes.add(lhp.lhp_code);
      sessionCount += lhp.matched_sessions.length;
    }
  }

  return { lhpCount: lhpCodes.size, sessionCount };
}

export function periodSpan(session: SessionScheduleItem): { start: string; span: number } {
  const periods = session.period_atomic?.length ? session.period_atomic : [session.period_code ?? "1"];
  return { start: periods[0], span: Math.max(1, periods.length) };
}

export function sessionDetailId(sessionId: string): string {
  return `session-${sessionId.replace(/[^a-zA-Z0-9_-]/g, "-")}`;
}

export function formatMilliseconds(value: number): string {
  if (value < 1000) {
    return `${value} ms`;
  }
  return `${(value / 1000).toFixed(2)} s`;
}

export function compactList(values: string[], limit = 3): string {
  const clean = values.filter(Boolean);
  if (clean.length <= limit) {
    return clean.join(", ");
  }
  return `${clean.slice(0, limit).join(", ")} +${clean.length - limit}`;
}
