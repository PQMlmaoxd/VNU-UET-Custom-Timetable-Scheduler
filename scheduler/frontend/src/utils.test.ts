import { describe, expect, it } from "vitest";

import type { AnchorCandidateGroup, DesiredAnchorSummary, SelectionRow } from "./types";
import {
  anchorCandidateIndexKey,
  buildAnchorCandidateIndex,
  buildCourseCatalog,
  clampTimeout,
  isSupportedTimetableFile,
  lhpOptionLabel,
  matchingCourses,
  normalizeText,
  normalizedLhpSchedules,
  selectedAssignments,
  sessionDetailId,
} from "./utils";

describe("frontend helpers", () => {
  it("normalizes Vietnamese search text", () => {
    expect(normalizeText("Mạng Máy Tính Đa phương tiện")).toBe("mang may tinh da phuong tien");
  });

  it("groups anchors into courses and searches without accents", () => {
    const catalog = buildCourseCatalog([
      { course_code: "INT2213", course_name: "Mạng Máy Tính", teaching_team_key: "team-a", teaching_team_label: "Nguyễn A", session_count: 2 },
      { course_code: "INT2213", course_name: "Mạng Máy Tính", teaching_team_key: "team-b", teaching_team_label: "Trần B", session_count: 2 },
      { course_code: "INT3306", course_name: "Formal Methods", teaching_team_key: "team-c", teaching_team_label: "Lê C", session_count: 1 },
    ]);

    expect(catalog).toHaveLength(2);
    expect(catalog[0].lecturers).toHaveLength(2);
    expect(matchingCourses(catalog, "mang may", new Set())).toHaveLength(1);
  });

  it("clamps timeout into the supported UI range", () => {
    expect(clampTimeout(1)).toBe(10);
    expect(clampTimeout(999)).toBe(300);
    expect(clampTimeout(Number.NaN)).toBe(180);
  });

  it("allows PDF only in the desktop application", () => {
    const pdf = new File(["%PDF-1.7"], "schedule.pdf", { type: "application/pdf" });
    expect(isSupportedTimetableFile(pdf, true)).toBe(true);
    expect(isSupportedTimetableFile(pdf, false)).toBe(false);
  });

  it("allows saved JSON timetables in both desktop and browser modes", () => {
    const json = new File(["{}"], "schedule.json", { type: "application/json" });
    expect(isSupportedTimetableFile(json, true)).toBe(true);
    expect(isSupportedTimetableFile(json, false)).toBe(true);
  });

  it("keeps punctuation variants in session IDs distinct", () => {
    expect(sessionDetailId("a.b")).not.toBe(sessionDetailId("a/b"));
  });

  it("falls back to matched_sessions when lhp_schedules is missing", () => {
    const anchor: DesiredAnchorSummary = {
      course_code: "INT2213",
      course_name: "Mạng Máy Tính",
      teaching_team_key: "team-a",
      teaching_team_label: "Nguyễn A",
      lhp_codes: ["INT2213-01"],
      session_count: 1,
      lhp_schedules: [],
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
      ],
    };

    expect(normalizedLhpSchedules(anchor)).toEqual([
      {
        lhp_code: "INT2213-01",
        session_count: 1,
        matched_sessions: anchor.matched_sessions,
      },
    ]);
  });

  it("builds an anchor candidate index keyed by course and teaching team", () => {
    const groups: AnchorCandidateGroup[] = [
      {
        course_code: "INT2213",
        teaching_team_key: "team-a",
        teaching_team_label: "Nguyễn A",
        candidates: [
          {
            lhp_code: "INT2213-01",
            teaching_unit_key: "unit-1",
            session_count: 1,
            slots: [{ day: 2, period_code: "1", timeslot_label: "Thứ 2 Ca 1" }],
          },
        ],
      },
    ];

    const index = buildAnchorCandidateIndex(groups);
    expect(index.get(anchorCandidateIndexKey("INT2213", "team-a"))).toEqual(groups[0].candidates);
    expect(index.has(anchorCandidateIndexKey("INT2213", "team-b"))).toBe(false);
    expect(buildAnchorCandidateIndex(undefined).size).toBe(0);
  });

  it("formats class section options with day, period, and session count", () => {
    expect(
      lhpOptionLabel({
        lhp_code: "INT2213-01",
        teaching_unit_key: "unit-1",
        session_count: 2,
        slots: [
          { day: 3, period_code: "2", timeslot_label: "Thứ 3 Ca 2" },
          { day: null, period_code: null, timeslot_label: "Online" },
        ],
      }),
    ).toBe("INT2213-01 · Thứ 3 Ca 2, Online · 2 buổi");
  });

  it("emits locked class section fields only when both values are present", () => {
    const base = {
      id: "row-1",
      courseKey: "INT2213::team-a",
      course_code: "INT2213",
      course_name: "Mạng Máy Tính",
      teaching_team_key: "team-a",
      teaching_team_label: "Nguyễn A",
    };
    expect(selectedAssignments([base as SelectionRow])).toHaveLength(1);

    const halfLocked = { ...base, locked_lhp_code: "INT2213-01" } as SelectionRow;
    const [halfPayload] = selectedAssignments([halfLocked]);
    expect(halfPayload.locked_lhp_code).toBeUndefined();
    expect(halfPayload.locked_teaching_unit_key).toBeUndefined();

    const locked = { ...base, locked_lhp_code: "INT2213-01", locked_unit_key: "unit-1" } as SelectionRow;
    const [payload] = selectedAssignments([locked]);
    expect(payload.locked_lhp_code).toBe("INT2213-01");
    expect(payload.locked_teaching_unit_key).toBe("unit-1");
  });
});
