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

export function UploadPanel({
  file,
  isLoading,
  onFileChange,
  onLoad,
  headingRef,
}: UploadPanelProps) {
  return (
    <section className="upload-layout" aria-labelledby="upload-heading">
      <div className="card primary-card">
        <p className="section-kicker">{COPY.upload.kicker}</p>
        <h2 id="upload-heading" ref={headingRef} tabIndex={-1}>{COPY.upload.heading}</h2>
        <p className="section-copy">{COPY.upload.description}</p>

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
            accept={isDesktopApp() ? ".xlsx,.pdf" : ".xlsx"}
            aria-label={COPY.upload.inputLabel}
            onChange={(event) => onFileChange(event.target.files?.[0] ?? null)}
          />
          <span className="dropzone-mark" aria-hidden="true">XLSX</span>
          <strong>{file ? file.name : COPY.upload.emptyPrompt}</strong>
          <span>{COPY.upload.supportedFormats(isDesktopApp())}</span>
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
