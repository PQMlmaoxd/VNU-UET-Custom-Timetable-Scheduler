import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { chromium } from "@playwright/test";

const base = process.argv[2] ?? "http://127.0.0.1:5174/";
const outDir = resolve(process.argv[3] ?? "../../.impeccable/review");
const prefix = process.argv[4] ?? "";
mkdirSync(outDir, { recursive: true });

const browser = await chromium.launch();
try {
  const desktop = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  await desktop.goto(base, { waitUntil: "networkidle" });
  await desktop.waitForTimeout(400);
  await desktop.screenshot({ path: `${outDir}/${prefix}desktop-light.png`, fullPage: true });

  await desktop.evaluate(() => localStorage.setItem("scheduler.theme", "dark"));
  await desktop.reload({ waitUntil: "networkidle" });
  await desktop.waitForTimeout(400);
  await desktop.screenshot({ path: `${outDir}/${prefix}desktop-dark.png`, fullPage: true });
  await desktop.close();

  const mobile = await browser.newPage({
    viewport: { width: 390, height: 844 },
    deviceScaleFactor: 2,
  });
  await mobile.goto(base, { waitUntil: "networkidle" });
  await mobile.waitForTimeout(400);
  await mobile.screenshot({ path: `${outDir}/${prefix}mobile-light.png`, fullPage: true });
  await mobile.close();
} finally {
  await browser.close();
}
console.log(`captured ${prefix}* into ${outDir}`);
