import { useId } from "react";

import type { StatusTone } from "../types";

export type AppStatus = {
  tone: StatusTone;
  title: string;
  detail: string;
};

export function Notice({ status }: { status: AppStatus }) {
  const titleId = useId();

  return (
    <>
      <section className="status-banner notice" data-tone={status.tone} aria-labelledby={titleId}>
        <span className="status-dot" aria-hidden="true" />
        <div>
          <strong id={titleId}>{status.title}</strong>
          <p>{status.detail}</p>
        </div>
      </section>
      <div
        className="sr-only status-live-region"
        role={status.tone === "error" ? "alert" : "status"}
        aria-live={status.tone === "error" ? "assertive" : "polite"}
        aria-atomic="true"
      >
        {status.title}. {status.detail}
      </div>
    </>
  );
}
