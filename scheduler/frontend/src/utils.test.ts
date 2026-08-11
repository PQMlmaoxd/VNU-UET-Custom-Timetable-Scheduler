import { describe, expect, it } from "vitest";

import type { DesiredAnchorSummary } from "./types";
import {
  buildCourseCatalog,
  clampTimeout,
  isSupportedTimetableFile,
  matchingCourses,
  normalizeText,
  normalizedLhpSchedules,
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
});
