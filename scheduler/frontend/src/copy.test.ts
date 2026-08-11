import { describe, expect, it } from "vitest";

import { COPY, userFacingError, userFacingNote } from "./copy";

describe("user-facing copy", () => {
  it("describes the current file capability without claiming PDF support", () => {
    expect(COPY.upload.inputLabel).toBe("Chọn hoặc thả thời khóa biểu");
    expect(COPY.upload.supportedFormats(false)).toBe("Hỗ trợ XLSX · PDF sẽ bổ sung");
    expect(COPY.upload.supportedFormats(true)).toBe("Hỗ trợ XLSX và PDF");
  });

  it("hides technical error details behind an actionable message", () => {
    expect(userFacingError(new Error("Workbook parser failed at Sheet3"), "Thử lại.")).toBe(
      "Không thể đọc thời khóa biểu. Hãy kiểm tra file XLSX rồi thử lại.",
    );
    expect(userFacingError(new Error("Native SolverWorker exited with code 1"), "Thử lại.")).toBe(
      "Không thể tìm thời khóa biểu. Hãy thử lại hoặc kiểm tra cài đặt ứng dụng.",
    );
    expect(userFacingError(new Error("Desktop bridge returned an invalid result."), "Thử lại.")).toBe(
      "Ứng dụng trả dữ liệu không hợp lệ. Hãy cập nhật ứng dụng rồi thử lại.",
    );
    expect(userFacingError(new Error("unexpected failure"), "Thử lại.")).toBe("Thử lại.");
  });

  it("translates known technical result notes and hides unknown internals", () => {
    expect(userFacingNote("HC-3 cohort collision")).toBe("Có buổi học trùng nhóm sinh viên.");
    expect(userFacingNote("Returned 2 feasible personal timetable(s).")).toBeNull();
    expect(userFacingNote("internal native detail")).toBeNull();
  });
});
