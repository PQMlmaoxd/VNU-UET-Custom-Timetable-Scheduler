import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

import { installDesktopBridge, loadResultsFromSavedJson } from "./support/bridge";

test.beforeEach(async ({ page }) => {
  installDesktopBridge(page);
});

function pinTheme(page: import("@playwright/test").Page, theme: "light" | "dark"): void {
  void page.addInitScript((nextTheme) => {
    localStorage.setItem("scheduler.theme", nextTheme);
  }, theme);
}

async function expectTheme(page: import("@playwright/test").Page, theme: "light" | "dark"): Promise<void> {
  await expect(page.locator("html")).toHaveAttribute("data-theme", theme);
}

test("upload screen matches approved baselines in both themes", async ({ page }, testInfo) => {
  test.skip(testInfo.project.name === "chromium-mobile", "Mobile upload is covered by the results baseline.");

    pinTheme(page, "light");
  await page.goto("/app/");
  await expectTheme(page, "light");
  await expect(page.getByRole("heading", { name: "Chọn file thời khóa biểu" })).toBeVisible();
  await expect(page).toHaveScreenshot(`${testInfo.project.name}-upload-light.png`, {
    animations: "disabled",
    maxDiffPixelRatio: 0.02,
  });

    pinTheme(page, "dark");
  await page.goto("/app/");
  await expectTheme(page, "dark");
  await expect(page).toHaveScreenshot(`${testInfo.project.name}-upload-dark.png`, {
    animations: "disabled",
    maxDiffPixelRatio: 0.02,
  });
});

test("results screen matches the approved light baseline", async ({ page }, testInfo) => {
  pinTheme(page, "light");
  await loadResultsFromSavedJson(page);
  await expectTheme(page, "light");
  await expect(page.getByRole("button", { name: /INT2213, INT2213-01/ })).toBeVisible();

  await expect(page).toHaveScreenshot(`${testInfo.project.name}-results-light.png`, {
    animations: "disabled",
    fullPage: true,
    maxDiffPixelRatio: 0.02,
  });
});

test("results screen matches the approved dark baseline and passes axe scan", async ({ page }, testInfo) => {
  pinTheme(page, "dark");
  await loadResultsFromSavedJson(page);
  await expectTheme(page, "dark");
  await expect(page.getByRole("button", { name: /INT2213, INT2213-01/ })).toBeVisible();

  await expect(page).toHaveScreenshot(`${testInfo.project.name}-results-dark.png`, {
    animations: "disabled",
    fullPage: true,
    maxDiffPixelRatio: 0.02,
  });

  const accessibilityScanResults = await new AxeBuilder({ page }).analyze();
  const seriousViolations = accessibilityScanResults.violations.filter((violation) =>
    violation.impact === "critical" || violation.impact === "serious",
  );
  expect(seriousViolations).toEqual([]);
});
