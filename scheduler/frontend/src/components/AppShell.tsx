import type { ReactNode } from "react";

import { COPY } from "../copy";
import logoUrl from "../assets/logo.png";
import type { ThemePreference } from "../theme";
import { Notice, type AppStatus } from "./Notice";

type AppShellProps = {
  file: File | null;
  themePreference: ThemePreference;
  status: AppStatus;
  onThemeChange: (preference: ThemePreference) => void;
  children: ReactNode;
};

export function AppShell({
  file,
  themePreference,
  status,
  onThemeChange,
  children,
}: AppShellProps) {
  return (
    <main className="app-shell">
      <a className="skip-link" href="#workspace">{COPY.accessibility.skipNavigation}</a>
      <header className="app-header" aria-labelledby="app-title">
        <div className="product-title">
          <img
            className="product-mark"
            src={logoUrl}
            alt=""
            width={42}
            height={32}
            aria-hidden="true"
          />
          <div>
            <p className="eyebrow">{COPY.product.eyebrow}</p>
            <h1 id="app-title">{COPY.product.title}</h1>
          </div>
        </div>
        <div className="header-actions">
          <ThemeControl preference={themePreference} onChange={onThemeChange} />
          <div className="workbook-badge" aria-label={COPY.product.currentFile}>
            <span>{COPY.product.currentFile}</span>
            <strong>{file?.name ?? COPY.product.noFile}</strong>
          </div>
        </div>
      </header>

      <Notice status={status} />
      {children}
    </main>
  );
}

function ThemeControl({
  preference,
  onChange,
}: {
  preference: ThemePreference;
  onChange: (preference: ThemePreference) => void;
}) {
  return (
    <label className="theme-control">
      <span>{COPY.theme.label}</span>
      <select value={preference} onChange={(event) => onChange(event.target.value as ThemePreference)}>
        <option value="system">{COPY.theme.system}</option>
        <option value="light">{COPY.theme.light}</option>
        <option value="dark">{COPY.theme.dark}</option>
      </select>
    </label>
  );
}
