import { afterEach, describe, expect, it } from "vitest";

import {
  THEME_STORAGE_KEY,
  applyTheme,
  readThemePreference,
  resolveTheme,
  saveThemePreference,
} from "./theme";

afterEach(() => {
  localStorage.clear();
  delete document.documentElement.dataset.theme;
  document.documentElement.style.colorScheme = "";
});

describe("theme", () => {
  it("resolves system preference from the operating-system mode", () => {
    expect(resolveTheme("system", true)).toBe("dark");
    expect(resolveTheme("system", false)).toBe("light");
    expect(resolveTheme("dark", false)).toBe("dark");
  });

  it("persists and applies an explicit preference", () => {
    saveThemePreference("dark");

    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe("dark");
    expect(readThemePreference()).toBe("dark");
    expect(applyTheme("dark", document.documentElement, false)).toBe("dark");
    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(document.documentElement.style.colorScheme).toBe("dark");
  });

  it("falls back to system when stored data is invalid", () => {
    localStorage.setItem(THEME_STORAGE_KEY, "sepia");

    expect(readThemePreference()).toBe("system");
  });
});
