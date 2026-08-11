export type ThemePreference = "system" | "light" | "dark";
export type ResolvedTheme = Exclude<ThemePreference, "system">;

export const THEME_STORAGE_KEY = "scheduler.theme";

export function isThemePreference(value: string | null): value is ThemePreference {
  return value === "system" || value === "light" || value === "dark";
}

export function resolveTheme(preference: ThemePreference, prefersDark: boolean): ResolvedTheme {
  if (preference === "system") {
    return prefersDark ? "dark" : "light";
  }

  return preference;
}

export function systemPrefersDark(): boolean {
  return typeof window.matchMedia === "function" && window.matchMedia("(prefers-color-scheme: dark)").matches;
}

export function readThemePreference(storage: Storage = window.localStorage): ThemePreference {
  const stored = storage.getItem(THEME_STORAGE_KEY);
  return isThemePreference(stored) ? stored : "system";
}

export function applyTheme(
  preference: ThemePreference,
  documentElement: HTMLElement = document.documentElement,
  prefersDark = systemPrefersDark(),
): ResolvedTheme {
  const resolved = resolveTheme(preference, prefersDark);
  documentElement.dataset.theme = resolved;
  documentElement.style.colorScheme = resolved;
  return resolved;
}

export function saveThemePreference(preference: ThemePreference, storage: Storage = window.localStorage): void {
  storage.setItem(THEME_STORAGE_KEY, preference);
}
