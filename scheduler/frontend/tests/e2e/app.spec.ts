import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

import {
  installDesktopBridge,
  loadResultsFromSavedJson,
  savedTimetableJson,
  solveResponse,
} from "./support/bridge";

test.beforeEach(async ({ page }) => {
  installDesktopBridge(page);
});

test("happy path is keyboard-readable and has no critical/serious axe issues", async ({ page }, testInfo) => {
  await page.goto("/app/");
  await page.setInputFiles('input[type="file"]', {
    name: "timetable.xlsx",
    mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    buffer: Buffer.from("mock workbook"),
  });

  await page.getByRole("button", { name: "Đọc thời khóa biểu" }).click();
  await expect(page.getByRole("heading", { name: /Chọn môn và nhóm giảng dạy/ })).toBeVisible();

  await page.getByPlaceholder(/Tìm theo mã hoặc tên môn/).fill("mang may");
  await page.getByRole("option", { name: /INT2213/ }).click();
  await expect(page.getByRole("combobox", { name: "Nhóm giảng dạy" })).toHaveValue("team-nguyen-a");

  await page.getByRole("button", { name: "Tiếp tục" }).click();
  await page.getByRole("button", { name: "Tìm thời khóa biểu" }).click();

  await expect(page.getByRole("heading", { name: "Đã tìm thấy lịch" })).toBeVisible();

  const solveRequest = await page.evaluate(() => {
    const bridgeWindow = window as Window & {
      __schedulerBridgeRequests?: Array<{ method: string; payload?: { desired_assignments?: Array<Record<string, string>> } }>;
    };
    return bridgeWindow.__schedulerBridgeRequests?.find(request => request.method === "solve_workbook");
  });
  expect(solveRequest?.payload?.desired_assignments?.[0]).toMatchObject({
    course_code: "INT2213",
    teaching_team_key: "team-nguyen-a",
    teaching_team_label: "Nguyễn A",
  });

  await expect(page.getByLabel("Buổi học trực tuyến").getByText("Trực tuyến")).toBeVisible();
  if (testInfo.project.name === "chromium-mobile") {
    await expect(page.getByLabel("Lịch theo ngày")).toContainText("INT2213");
  } else {
    await expect(page.getByRole("button", { name: /INT2213, INT2213-01/ })).toBeVisible();
  }

  const accessibilityScanResults = await new AxeBuilder({ page }).analyze();
  const seriousViolations = accessibilityScanResults.violations.filter((violation) =>
    violation.impact === "critical" || violation.impact === "serious",
  );
  expect(seriousViolations).toEqual([]);
});

test("locks a class section and sends the locked selection to the solver", async ({ page }) => {
  await page.goto("/app/");
  await page.setInputFiles('input[type="file"]', {
    name: "timetable.xlsx",
    mimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    buffer: Buffer.from("mock workbook"),
  });

  await page.getByRole("button", { name: "Đọc thời khóa biểu" }).click();
  await expect(page.getByRole("heading", { name: /Chọn môn và nhóm giảng dạy/ })).toBeVisible();

  const lockSelect = page.getByRole("combobox", { name: "Khóa lớp học phần" });
  await expect(lockSelect).toBeDisabled();

  await page.getByPlaceholder(/Tìm theo mã hoặc tên môn/).fill("mang may");
  await page.getByRole("option", { name: /INT2213/ }).click();
  await expect(lockSelect).toBeEnabled();
  await expect(lockSelect).toContainText(/INT2213-01 · .* · 2 buổi/u);

  await lockSelect.selectOption("unit:int2213-01");
  await expect(page.locator(".summary-strip")).toContainText("khóa INT2213-01");

  await page.getByRole("button", { name: "Tiếp tục" }).click();
  await page.getByRole("button", { name: "Tìm thời khóa biểu" }).click();

  await expect(page.getByRole("heading", { name: "Đã tìm thấy lịch" })).toBeVisible();

  const solveRequest = await page.evaluate(() => {
    const bridgeWindow = window as Window & {
      __schedulerBridgeRequests?: Array<{ method: string; payload?: { desired_assignments?: Array<Record<string, unknown>> } }>;
    };
    return bridgeWindow.__schedulerBridgeRequests?.find(request => request.method === "solve_workbook");
  });
  expect(solveRequest?.payload?.desired_assignments?.[0]).toMatchObject({
    course_code: "INT2213",
    teaching_team_key: "team-nguyen-a",
    locked_lhp_code: "INT2213-01",
    locked_teaching_unit_key: "unit:int2213-01",
  });
});

test("loads a saved JSON timetable directly into results", async ({ page }, testInfo) => {
  await loadResultsFromSavedJson(page);
  await expect(page.getByRole("heading", { name: /Chọn môn và nhóm giảng dạy/ })).not.toBeVisible();
  if (testInfo.project.name === "chromium-mobile") {
    await expect(page.getByLabel("Lịch theo ngày")).toContainText("INT2213");
  } else {
    await expect(page.getByRole("button", { name: /INT2213, INT2213-01/ })).toBeVisible();
  }
});

test("saves a loaded SAT timetable through the desktop bridge", async ({ page }) => {
  await loadResultsFromSavedJson(page);
  await page.getByRole("button", { name: "Lưu thời khóa biểu (.json)" }).click();
  await expect(page.getByText("Đã lưu TKB-ALL-20260821-1530.json.", { exact: true })).toBeVisible();

  const saveRequest = await page.evaluate(() => {
    const bridgeWindow = window as Window & {
      __schedulerBridgeRequests?: Array<{ method: string; payload?: { source_file_name?: string } }>;
    };
    return bridgeWindow.__schedulerBridgeRequests?.find(request => request.method === "save_timetable");
  });
  expect(saveRequest?.payload?.source_file_name).toBe("timetable.xlsx");
});

test("persists dark theme and keeps the laptop workspace bounded", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === "chromium-mobile", "Wide-screen geometry is covered by the desktop project.");
  await page.setViewportSize({ width: 1366, height: 768 });
  await page.goto("/app/");

  await page.getByLabel("Giao diện").selectOption("dark");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");
  await page.reload();
  await expect(page.getByLabel("Giao diện")).toHaveValue("dark");
  await expect(page.locator("html")).toHaveAttribute("data-theme", "dark");

  const laptopGeometry = await page.evaluate(() => ({
    appWidth: document.querySelector(".app-shell")?.getBoundingClientRect().width ?? 0,
    horizontalOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
  }));
  expect(laptopGeometry.appWidth).toBeLessThanOrEqual(1440);
  expect(laptopGeometry.horizontalOverflow).toBe(false);

  await page.setViewportSize({ width: 1920, height: 1080 });
  const wideGeometry = await page.evaluate(() => ({
    appWidth: document.querySelector(".app-shell")?.getBoundingClientRect().width ?? 0,
    horizontalOverflow: document.documentElement.scrollWidth > document.documentElement.clientWidth,
  }));
  expect(wideGeometry.appWidth).toBeLessThanOrEqual(1440);
  expect(wideGeometry.horizontalOverflow).toBe(false);

  const darkThemeScan = await new AxeBuilder({ page }).analyze();
  const seriousDarkThemeViolations = darkThemeScan.violations.filter((violation) =>
    violation.impact === "critical" || violation.impact === "serious",
  );
  expect(seriousDarkThemeViolations).toEqual([]);
});

test("saved JSON keeps strict document validation", async ({ page }) => {
  await page.goto("/app/");
  await page.setInputFiles('input[type="file"]', {
    name: "broken.json",
    mimeType: "application/json",
    buffer: Buffer.from(savedTimetableJson({ ...solveResponse, solver: { ...solveResponse.solver, backend: "\uFFFD" } })),
  });
  await page.getByRole("button", { name: "Đọc thời khóa biểu" }).click();
  await expect(page.getByText("File JSON không đúng định dạng thời khóa biểu đã lưu.", { exact: true })).toBeVisible();
});
