import type { RefObject } from "react";

import { isDesktopApp } from "../../api";
import { COPY } from "../../copy";

type UploadPanelProps = {
  file: File | null;
  isLoading: boolean;
  onFileChange: (file: File | null) => void;
  onLoad: () => void;
  headingRef: RefObject<HTMLHeadingElement>;
};

const DESKTOP_FORMATS = ["XLSX", "PDF", "JSON"];
const WEB_FORMATS = ["XLSX", "JSON"];

export function UploadPanel({
  file,
  isLoading,
  onFileChange,
  onLoad,
  headingRef,
}: UploadPanelProps) {
  const formats = isDesktopApp() ? DESKTOP_FORMATS : WEB_FORMATS;

  return (
    <section className="upload-layout" aria-labelledby="upload-heading">
      <div className="panel panel-pad">
        <h2 id="upload-heading" ref={headingRef} tabIndex={-1}>{COPY.upload.heading}</h2>
        <p className="stage-copy">{COPY.upload.description}</p>

        <label
          className="dropzone"
          onDragOver={(event) => {
            event.preventDefault();
            event.currentTarget.dataset.dragging = "true";
          }}
          onDragLeave={(event) => {
            delete event.currentTarget.dataset.dragging;
          }}
          onDrop={(event) => {
            event.preventDefault();
            delete event.currentTarget.dataset.dragging;
            onFileChange(event.dataTransfer.files.item(0));
          }}
        >
          <input
            className="sr-only"
            type="file"
            accept={isDesktopApp() ? ".xlsx,.pdf,.json" : ".xlsx,.json"}
            aria-label={COPY.upload.inputLabel}
            onChange={(event) => onFileChange(event.target.files?.[0] ?? null)}
          />
          <strong>{file ? file.name : COPY.upload.emptyPrompt}</strong>
          <span className="dropzone-hint">{COPY.upload.supportedFormats(isDesktopApp())}</span>
          <span className="format-row" aria-hidden="true">
            {formats.map((format) => (
              <span className="format-chip" key={format}>{format}</span>
            ))}
          </span>
        </label>

        <div className="action-row">
          <button className="button primary" type="button" disabled={!file || isLoading} onClick={onLoad}>
            {isLoading ? COPY.upload.loadingTitle : COPY.upload.readButton}
          </button>
        </div>
      </div>
    </section>
  );
}
