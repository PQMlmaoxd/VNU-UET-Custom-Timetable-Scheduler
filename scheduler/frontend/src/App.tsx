import { useEffect, useId, useMemo, useRef, useState, type RefObject } from "react";

import {
  exportUnsatArtifact,
  isDesktopApp,
  notifyDesktopReady,
  setDesktopTheme,
  solveWorkbook,
  validateWorkbook,
} from "./api";
import { COPY, userFacingError, userFacingNote } from "./copy";
import {
  applyTheme,
  readThemePreference,
  saveThemePreference,
  type ThemePreference,
} from "./theme";
import type {
  CourseCatalogItem,
  DesiredAssignmentPayload,
  RescheduleResponse,
  SelectionRow,
  StatusTone,
  StepId,
  ValidateExistingResponse,
} from "./types";
import {
  DAYS,
  DAY_LABELS,
  PERIODS,
  PERIOD_LABELS,
  TIMEOUT_DEFAULT,
  TIMEOUT_MAX,
  TIMEOUT_MIN,
  buildCourseCatalog,
  clampTimeout,
  compactList,
  courseLabel,
  formatMilliseconds,
  hasIncompleteSelections,
  isSupportedTimetableFile,
  matchingCourses,
  normalizedLhpSchedules,
  onlineSessionsForSolution,
  periodSpan,
  physicalSessionsForSolution,
  selectedAssignments,
  sessionDetailId,
  solutionStats,
} from "./utils";

type AppStatus = {
  tone: StatusTone;
  title: string;
  detail: string;
};

type SolveState = "idle" | "busy" | "done" | "error";

const STEPS: { id: StepId; label: string; title: string }[] = [
  { id: 1, label: "01", title: COPY.steps.upload },
  { id: 2, label: "02", title: COPY.steps.selection },
  { id: 3, label: "03", title: COPY.steps.results },
];

const TIMEOUT_PRESETS = [
  { label: COPY.solve.presets.quick, value: 30, note: COPY.solve.presets.quickNote },
  { label: COPY.solve.presets.balanced, value: 180, note: COPY.solve.presets.balancedNote },
  { label: COPY.solve.presets.thorough, value: 300, note: COPY.solve.presets.thoroughNote },
];

function blankSelection(): SelectionRow {
  return {
    id: crypto.randomUUID(),
    courseKey: "",
    course_code: "",
    course_name: "",
    teaching_team_key: "",
    teaching_team_label: "",
  };
}

function App() {
  const [currentStep, setCurrentStep] = useState<StepId>(1);
  const [file, setFile] = useState<File | null>(null);
  const [workbook, setWorkbook] = useState<ValidateExistingResponse | null>(null);
  const [rows, setRows] = useState<SelectionRow[]>([blankSelection()]);
  const [timeoutSeconds, setTimeoutSeconds] = useState(TIMEOUT_DEFAULT);
  const [themePreference, setThemePreference] = useState<ThemePreference>(() => readThemePreference());
  const [status, setStatus] = useState<AppStatus>({
    tone: "idle",
    title: COPY.upload.missingTitle,
    detail: COPY.upload.missingDetail,
  });
  const [isLoadingWorkbook, setIsLoadingWorkbook] = useState(false);
  const [solveState, setSolveState] = useState<SolveState>("idle");
  const [solveResponse, setSolveResponse] = useState<RescheduleResponse | null>(null);
  const [isExportingUnsat, setIsExportingUnsat] = useState(false);
  const [solutionIndex, setSolutionIndex] = useState(0);
  const [elapsedMs, setElapsedMs] = useState(0);
  const solveCancellation = useRef<AbortController | null>(null);
  const exportCancellation = useRef<AbortController | null>(null);
  const workbookCancellation = useRef<AbortController | null>(null);
  const validationRequest = useRef(0);
  const stageHeading = useRef<HTMLHeadingElement | null>(null);
  const hasNotifiedDesktopReady = useRef(false);

  const catalog = useMemo(
    () => workbook ? buildCourseCatalog(workbook.prototype_catalog.anchors) : [],
    [workbook],
  );
  const assignments = useMemo(() => selectedAssignments(rows), [rows]);
  const hasIncompleteRows = hasIncompleteSelections(rows);
  const fatalWarningCount = workbook?.parse_summary.fatal_warning_count ?? 0;
  const maxStep = getMaxStep(Boolean(workbook), assignments.length, hasIncompleteRows, fatalWarningCount);
  const clampedCurrentStep = currentStep > maxStep ? maxStep : currentStep;

  useEffect(() => {
    if (currentStep !== clampedCurrentStep) {
      setCurrentStep(clampedCurrentStep);
    }
  }, [clampedCurrentStep, currentStep]);

  useEffect(() => {
    const mediaQuery = typeof window.matchMedia === "function"
      ? window.matchMedia("(prefers-color-scheme: dark)")
      : null;
    const applyCurrentTheme = () => applyTheme(themePreference, document.documentElement, mediaQuery?.matches ?? false);
    const handleSystemThemeChange = () => {
      if (themePreference === "system") {
        applyCurrentTheme();
        void setDesktopTheme("system").catch(() => undefined);
      }
    };

    applyCurrentTheme();
    mediaQuery?.addEventListener("change", handleSystemThemeChange);
    void setDesktopTheme(themePreference).catch(() => undefined);
    return () => mediaQuery?.removeEventListener("change", handleSystemThemeChange);
  }, [themePreference]);

  useEffect(() => {
    if (hasNotifiedDesktopReady.current) {
      return;
    }

    hasNotifiedDesktopReady.current = true;
    void notifyDesktopReady().catch(() => undefined);
  }, []);

  useEffect(() => {
    stageHeading.current?.focus();
  }, [currentStep, solveState, solveResponse]);

  useEffect(() => {
    if (solveState !== "busy") {
      return;
    }

    const startedAt = Date.now();
    const timer = window.setInterval(() => {
      setElapsedMs(Date.now() - startedAt);
    }, 250);
    return () => window.clearInterval(timer);
  }, [solveState]);

  function resetWorkbookState(nextFile: File | null) {
    validationRequest.current += 1;
    workbookCancellation.current?.abort();
    workbookCancellation.current = null;
    solveCancellation.current?.abort();
    solveCancellation.current = null;
    exportCancellation.current?.abort();
    exportCancellation.current = null;
    setIsExportingUnsat(false);
    setFile(nextFile);
    setWorkbook(null);
    setRows([blankSelection()]);
    setSolveResponse(null);
    setSolutionIndex(0);
    setSolveState("idle");
    setElapsedMs(0);
    if (!nextFile) {
      setStatus({
        tone: "idle",
        title: COPY.upload.missingTitle,
        detail: COPY.upload.missingDetail,
      });
    }
    setIsLoadingWorkbook(false);
    setCurrentStep(1);
  }

  function acceptFile(nextFile: File | null) {
    if (!nextFile) {
      resetWorkbookState(null);
      setStatus({
        tone: "idle",
        title: COPY.upload.missingTitle,
        detail: COPY.upload.missingDetail,
      });
      return;
    }

    if (!isSupportedTimetableFile(nextFile, isDesktopApp())) {
      resetWorkbookState(null);
      setStatus({
        tone: "error",
        title: COPY.upload.invalidTitle,
        detail: COPY.upload.invalidDetail(isDesktopApp()),
      });
      return;
    }

    resetWorkbookState(nextFile);

    setStatus({
      tone: "idle",
      title: COPY.upload.readyTitle,
      detail: nextFile.name,
    });
  }

  async function loadWorkbook() {
    if (!file) {
      setStatus({
        tone: "error",
        title: COPY.upload.missingTitle,
        detail: COPY.upload.missingDetail,
      });
      return;
    }

    if (!isSupportedTimetableFile(file, isDesktopApp())) {
      setStatus({
        tone: "error",
        title: COPY.upload.invalidTitle,
        detail: COPY.upload.invalidDetail(isDesktopApp()),
      });
      return;
    }

    workbookCancellation.current?.abort();
    const cancellation = new AbortController();
    workbookCancellation.current = cancellation;
    const requestId = validationRequest.current + 1;
    validationRequest.current = requestId;
    setIsLoadingWorkbook(true);
    setStatus({ tone: "busy", title: COPY.upload.loadingTitle, detail: COPY.upload.loadingDetail });

    try {
      const response = await validateWorkbook(file, cancellation.signal);
      if (validationRequest.current !== requestId || cancellation.signal.aborted) {
        return;
      }
      setWorkbook(response);
      setRows([blankSelection()]);
      setCurrentStep(2);
      setStatus({
        tone: "success",
        title: COPY.upload.successTitle,
        detail: COPY.upload.successDetail(
          response.parse_summary.anchor_count,
          response.parse_summary.schedulable_sessions,
        ),
      });
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }
      if (validationRequest.current !== requestId) {
        return;
      }
      setStatus({
        tone: "error",
        title: COPY.upload.errorTitle,
        detail: userFacingError(error, "Kiểm tra lại file rồi thử lại."),
      });
    } finally {
      if (validationRequest.current === requestId) {
        setIsLoadingWorkbook(false);
        workbookCancellation.current = null;
      }
    }
  }

  function updateCourse(rowId: string, course: CourseCatalogItem) {
    setRows((currentRows) =>
      currentRows.map((row) =>
        row.id === rowId
          ? {
              ...row,
              courseKey: course.key,
              course_code: course.course_code,
              course_name: course.course_name,
               teaching_team_key: course.lecturers.length === 1 ? course.lecturers[0].teaching_team_key : "",
               teaching_team_label: course.lecturers.length === 1 ? course.lecturers[0].teaching_team_label : "",
            }
          : row,
      ),
    );
    resetSolveOnly();
  }

  function updateTeachingTeam(rowId: string, teachingTeamKey: string) {
    setRows((currentRows) =>
      currentRows.map((row) => {
        if (row.id !== rowId) {
          return row;
        }

        const course = catalog.find((item) => item.key === row.courseKey);
        const team = course?.lecturers.find((item) => item.teaching_team_key === teachingTeamKey);
        return {
          ...row,
          teaching_team_key: team?.teaching_team_key ?? "",
          teaching_team_label: team?.teaching_team_label ?? "",
        };
      }),
    );
    resetSolveOnly();
  }

  function clearCourse(rowId: string) {
    setRows((currentRows) =>
      currentRows.map((row) =>
        row.id === rowId
          ? {
              ...row,
              courseKey: "",
              course_code: "",
              course_name: "",
              teaching_team_key: "",
              teaching_team_label: "",
            }
          : row,
      ),
    );
    resetSolveOnly();
  }

  function addSelectionRow() {
    setRows((currentRows) => [...currentRows, blankSelection()]);
    resetSolveOnly();
  }

  function removeSelectionRow(rowId: string) {
    setRows((currentRows) => {
      const nextRows = currentRows.filter((row) => row.id !== rowId);
      return nextRows.length > 0 ? nextRows : [blankSelection()];
    });
    resetSolveOnly();
  }

  function resetSolveOnly() {
    solveCancellation.current?.abort();
    solveCancellation.current = null;
    exportCancellation.current?.abort();
    exportCancellation.current = null;
    setIsExportingUnsat(false);
    setSolveResponse(null);
    setSolutionIndex(0);
    setSolveState("idle");
    setElapsedMs(0);
    if (workbook) {
      setStatus({
        tone: "idle",
        title: COPY.selection.changedTitle,
        detail: COPY.selection.changedDetail,
      });
    }
  }

  function returnToSelection() {
    resetSolveOnly();
    setCurrentStep(2);
  }

  async function solve() {
    if (!file || !workbook) {
      setStatus({ tone: "error", title: COPY.solve.missingTitle, detail: COPY.solve.missingDetail });
      return;
    }

    const desiredAssignments = selectedAssignments(rows);
    if (desiredAssignments.length === 0 || hasIncompleteSelections(rows)) {
      setStatus({
        tone: "error",
        title: COPY.solve.invalidTitle,
        detail: COPY.solve.invalidDetail,
      });
      setCurrentStep(2);
      return;
    }

    if (workbook.parse_summary.fatal_warning_count > 0) {
      setStatus({
        tone: "error",
        title: COPY.selection.fatalRows,
        detail: COPY.selection.fatalRowDetails,
      });
      setCurrentStep(2);
      return;
    }

    const timeout = clampTimeout(timeoutSeconds);
    setTimeoutSeconds(timeout);
    setSolveState("busy");
    setSolveResponse(null);
    setSolutionIndex(0);
    setElapsedMs(0);
    setCurrentStep(3);
    setStatus({
      tone: "busy",
      title: COPY.solve.busyStart,
      detail: COPY.solve.busyDetail,
    });

    const cancellation = new AbortController();
    solveCancellation.current = cancellation;
    try {
      const response = await solveWorkbook(file, desiredAssignments, timeout, cancellation.signal);
      if (solveCancellation.current !== cancellation) {
        return;
      }
      setSolveResponse(response);
      setSolveState("done");
      setStatus(resultStatus(response));
    } catch (error) {
      if (solveCancellation.current !== cancellation) {
        return;
      }
      if (error instanceof DOMException && error.name === "AbortError") {
        setSolveState("idle");
        setCurrentStep(2);
        setStatus({
          tone: "warning",
          title: COPY.solve.cancelledTitle,
          detail: COPY.solve.cancelledDetail,
        });
        return;
      }
      setSolveState("error");
      setStatus({
        tone: "error",
        title: COPY.solve.errorTitle,
        detail: userFacingError(error, "Kiểm tra lại lựa chọn rồi thử lại."),
      });
    } finally {
      if (solveCancellation.current === cancellation) {
        solveCancellation.current = null;
      }
    }
  }

  function cancelSolve() {
    if (!solveCancellation.current) {
      return;
    }

    setStatus({
      tone: "busy",
      title: COPY.solve.cancelling,
      detail: COPY.solve.cancellingDetail,
    });
    solveCancellation.current.abort();
  }

  async function exportFormalUnsat() {
    const verificationToken = solveResponse?.solver.formal_verification_token;
    if (!file || !solveResponse || solveResponse.solver.satisfiability !== "UNSAT" || !verificationToken || !isDesktopApp()) {
      return;
    }

    setIsExportingUnsat(true);
    exportCancellation.current?.abort();
    const cancellation = new AbortController();
    exportCancellation.current = cancellation;
    try {
      const result = await exportUnsatArtifact(file, assignments, verificationToken, cancellation.signal);
      if (exportCancellation.current !== cancellation) {
        return;
      }
      if (!result.exported || !result.file_name) {
        setStatus({
          tone: "idle",
          title: COPY.results.exportCancelled,
          detail: COPY.results.exportCancelled,
        });
        return;
      }

      setStatus({
        tone: "success",
        title: COPY.results.exportedUnsatTitle,
        detail: COPY.results.exportedUnsatDetail(result.file_name),
      });
    } catch (error) {
      if (exportCancellation.current !== cancellation) {
        return;
      }
      if (error instanceof DOMException && error.name === "AbortError") {
        return;
      }
      setStatus({
        tone: "error",
        title: COPY.results.exportErrorTitle,
        detail: userFacingError(error, COPY.results.exportErrorDetail),
      });
    } finally {
      if (exportCancellation.current === cancellation) {
        exportCancellation.current = null;
        setIsExportingUnsat(false);
      }
    }
  }

  function changeTheme(preference: ThemePreference) {
    saveThemePreference(preference);
    setThemePreference(preference);
  }

  return (
    <main className="app-shell">
      <a className="skip-link" href="#workspace">{COPY.accessibility.skipNavigation}</a>
      <header className="app-header" aria-labelledby="app-title">
        <div className="product-title">
          <span className="product-mark" aria-hidden="true">TS</span>
          <div>
            <p className="eyebrow">{COPY.product.eyebrow}</p>
            <h1 id="app-title">{COPY.product.title}</h1>
          </div>
        </div>
        <div className="header-actions">
          <ThemeControl preference={themePreference} onChange={changeTheme} />
          <div className="workbook-badge" aria-label={COPY.product.currentFile}>
            <span>{COPY.product.currentFile}</span>
            <strong>{file?.name ?? COPY.product.noFile}</strong>
          </div>
        </div>
      </header>

      <StatusBanner status={status} />

      <nav className="stepper" aria-label={COPY.steps.navigationLabel}>
        {STEPS.map((step) => {
          const isActive = step.id === currentStep;
          const isDone = step.id < currentStep && step.id <= maxStep;
          const isDisabled = step.id > maxStep;
          return (
            <button
              className="step-button"
              type="button"
              key={step.id}
              disabled={isDisabled}
              aria-current={isActive ? "step" : undefined}
              data-active={isActive || undefined}
              data-done={isDone || undefined}
              onClick={() => setCurrentStep(step.id)}
            >
              <span>{isDone ? "✓" : step.label}</span>
              <strong>{step.title}</strong>
            </button>
          );
        })}
      </nav>

      <div id="workspace" className="workspace" tabIndex={-1}>
      {currentStep === 1 ? (
        <UploadPanel
          file={file}
          isLoading={isLoadingWorkbook}
          onFileChange={acceptFile}
          onLoad={loadWorkbook}
          headingRef={stageHeading}
        />
      ) : null}

      {currentStep === 2 && workbook ? (
        <SelectionPanel
          catalog={catalog}
          rows={rows}
          assignments={assignments}
          validation={workbook.existing_schedule_validation}
          skippedRows={workbook.parse_summary.skipped_rows}
          fatalWarnings={workbook.parse_summary.fatal_warning_count}
          fatalWarningMessages={workbook.parse_summary.fatal_warnings}
          quarantinedOfferings={workbook.parse_summary.quarantined_offerings}
          onCourseChange={updateCourse}
          onCourseClear={clearCourse}
          onLecturerChange={updateTeachingTeam}
          onAdd={addSelectionRow}
          onRemove={removeSelectionRow}
          hasIncompleteRows={hasIncompleteRows}
          onContinue={() => setCurrentStep(3)}
          headingRef={stageHeading}
        />
      ) : null}

      {currentStep === 3 && workbook && solveState !== "busy" && !solveResponse ? (
        <SolvePanel
          rows={rows}
          assignments={assignments}
          timeoutSeconds={timeoutSeconds}
          solveState={solveState}
          onTimeoutChange={setTimeoutSeconds}
          onBack={returnToSelection}
          onSolve={solve}
          hasIncompleteRows={hasIncompleteRows}
          headingRef={stageHeading}
        />
      ) : null}

      {currentStep === 3 && (solveState === "busy" || solveResponse || solveState === "error") ? (
        <ResultsPanel
          solveState={solveState}
          response={solveResponse}
          solutionIndex={solutionIndex}
          elapsedMs={elapsedMs}
          timeoutSeconds={timeoutSeconds}
          onSolutionChange={setSolutionIndex}
          onCancel={cancelSolve}
          onExportUnsat={exportFormalUnsat}
          isExportingUnsat={isExportingUnsat}
          onBackToSelection={returnToSelection}
          onReset={() => resetWorkbookState(null)}
          headingRef={stageHeading}
        />
      ) : null}
      </div>
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

function getMaxStep(
  hasWorkbook: boolean,
  assignmentCount: number,
  hasIncompleteRows: boolean,
  fatalWarningCount: number,
): StepId {
  if (!hasWorkbook) {
    return 1;
  }
  if (assignmentCount === 0 || hasIncompleteRows || fatalWarningCount > 0) {
    return 2;
  }
  return 3;
}

function resultStatus(response: RescheduleResponse): AppStatus {
  if (response.solver.status === "timeout") {
    return {
      tone: "warning",
      title: COPY.results.timeoutTitle,
      detail: COPY.results.timeoutDetail,
    };
  }

  if (response.solver.satisfiability === "SAT" && response.solutions.length > 0) {
    return {
      tone: "success",
      title: COPY.results.validHeading,
      detail: COPY.results.firstSolutionDetail(response.solutions.length, response.solutions[0].movement_cost),
    };
  }

  if (response.solver.satisfiability === "UNSAT") {
    return {
      tone: "warning",
      title: COPY.results.noScheduleHeading,
      detail: COPY.results.noScheduleDetail,
    };
  }

  return {
    tone: "warning",
    title: COPY.results.uncertainHeading,
    detail: COPY.results.uncertainDetail,
  };
}

function StatusBanner({ status }: { status: AppStatus }) {
  return (
    <section
      className="status-banner"
      data-tone={status.tone}
      role={status.tone === "error" ? "alert" : "status"}
      aria-live={status.tone === "error" ? "assertive" : "polite"}
    >
      <span className="status-dot" aria-hidden="true" />
      <div>
        <strong>{status.title}</strong>
        <p>{status.detail}</p>
      </div>
    </section>
  );
}

function UploadPanel({
  file,
  isLoading,
  onFileChange,
  onLoad,
  headingRef,
}: {
  file: File | null;
  isLoading: boolean;
  onFileChange: (file: File | null) => void;
  onLoad: () => void;
  headingRef: RefObject<HTMLHeadingElement>;
}) {
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

function SelectionPanel({
  catalog,
  rows,
  assignments,
  validation,
  skippedRows,
  fatalWarnings,
  fatalWarningMessages,
  quarantinedOfferings,
  onCourseChange,
  onCourseClear,
  onLecturerChange,
  onAdd,
  onRemove,
  hasIncompleteRows,
  onContinue,
  headingRef,
}: {
  catalog: CourseCatalogItem[];
  rows: SelectionRow[];
  assignments: DesiredAssignmentPayload[];
  validation: ValidateExistingResponse["existing_schedule_validation"];
  skippedRows: number;
  fatalWarnings: number;
  fatalWarningMessages: string[];
  quarantinedOfferings: ValidateExistingResponse["parse_summary"]["quarantined_offerings"];
  onCourseChange: (rowId: string, course: CourseCatalogItem) => void;
  onCourseClear: (rowId: string) => void;
  onLecturerChange: (rowId: string, lecturerName: string) => void;
  onAdd: () => void;
  onRemove: (rowId: string) => void;
  hasIncompleteRows: boolean;
  onContinue: () => void;
  headingRef: RefObject<HTMLHeadingElement>;
}) {
  return (
    <section className="panel-grid" aria-labelledby="selection-heading">
      <div className="card primary-card">
        <p className="section-kicker">{COPY.selection.kicker}</p>
        <h2 id="selection-heading" ref={headingRef} tabIndex={-1}>{COPY.selection.heading}</h2>
        <p className="section-copy">{COPY.selection.description}</p>

        {!validation.is_valid || skippedRows > 0 || fatalWarnings > 0 || quarantinedOfferings.length > 0 ? (
          <aside className="import-warning" role="status" aria-live="polite">
            {!validation.is_valid ? (
              <>
                <strong>{COPY.selection.validationWarning(validation.violation_count)}</strong>
                <p>{COPY.selection.validationDetail}</p>
                {validation.sample_violations.length > 0 ? (
                  <ul>
                    {validation.sample_violations.slice(0, 3).map((violation) => (
                      <li key={violation}>{violation}</li>
                    ))}
                  </ul>
                ) : null}
              </>
            ) : null}
            {skippedRows > 0 ? <p>{COPY.selection.skippedRows(skippedRows)}</p> : null}
            {quarantinedOfferings.length > 0 ? (
              <>
                <p>{COPY.selection.partialImport(quarantinedOfferings.length)}</p>
                <strong>{COPY.selection.partialImportDetails}</strong>
                <ul>
                  {quarantinedOfferings.slice(0, 10).map((offering) => (
                    <li key={`${offering.course_code}-${offering.lhp_code}`}>
                      {offering.course_code} · {offering.lhp_code} ({offering.quarantined_row_count} dòng)
                    </li>
                  ))}
                </ul>
              </>
            ) : null}
            {fatalWarnings > 0 ? (
              <>
                <p>{COPY.selection.fatalRows}</p>
                {fatalWarningMessages.length > 0 ? (
                  <>
                    <strong>{COPY.selection.fatalRowDetails}</strong>
                    <ul>
                      {fatalWarningMessages.slice(0, 10).map((warning) => (
                        <li key={warning}>{warning}</li>
                      ))}
                    </ul>
                  </>
                ) : null}
              </>
            ) : null}
          </aside>
        ) : null}

        <div className="selection-list">
          {rows.map((row, index) => {
            const selectedKeys = new Set(rows.filter((item) => item.id !== row.id).map((item) => item.courseKey).filter(Boolean));
            const selectedCourse = catalog.find((course) => course.key === row.courseKey) ?? null;
            return (
              <div className="selection-row" key={row.id}>
                <div className="row-index" aria-hidden="true">{index + 1}</div>
                <CourseCombobox
                  row={row}
                  catalog={catalog}
                  selectedKeys={selectedKeys}
                  onChange={(course) => onCourseChange(row.id, course)}
                  onClear={() => onCourseClear(row.id)}
                />
                <label className="field">
                  <span>{COPY.selection.lecturerLabel}</span>
                  <select
                    value={row.teaching_team_key}
                    disabled={!selectedCourse}
                    onChange={(event) => onLecturerChange(row.id, event.target.value)}
                  >
                    <option value="">{COPY.selection.lecturerPlaceholder}</option>
                    {selectedCourse?.lecturers.map((lecturer) => (
                      <option key={lecturer.teaching_team_key} value={lecturer.teaching_team_key}>
                        {lecturer.teaching_team_label} · {lecturer.session_count} buổi
                      </option>
                    ))}
                  </select>
                </label>
                <button className="button ghost compact" type="button" onClick={() => onRemove(row.id)} aria-label={COPY.selection.removeCourse(index + 1)}>
                  {COPY.selection.removeButton}
                </button>
              </div>
            );
          })}
        </div>

        {hasIncompleteRows ? (
          <p className="inline-warning" role="status">
            {COPY.selection.incomplete}
          </p>
        ) : null}

        <div className="action-row split">
          <button className="button secondary" type="button" onClick={onAdd}>{COPY.selection.addCourse}</button>
          <button
            className="button primary"
            type="button"
            disabled={assignments.length === 0 || hasIncompleteRows || fatalWarnings > 0}
            onClick={onContinue}
          >
            {COPY.selection.continue}
          </button>
        </div>
      </div>

      <aside className="card rail-card" aria-label={COPY.selection.summaryLabel}>
        <h3>{COPY.selection.summaryHeading}</h3>
        {assignments.length > 0 ? (
          <ul className="compact-list">
            {assignments.map((assignment) => (
              <li key={`${assignment.course_code}-${assignment.teaching_team_key}`}>
                <strong>{assignment.course_code}</strong>
                <span>{assignment.teaching_team_label}</span>
              </li>
            ))}
          </ul>
        ) : (
          <EmptyRail text={COPY.selection.emptySummary} />
        )}
      </aside>
    </section>
  );
}

function CourseCombobox({
  row,
  catalog,
  selectedKeys,
  onChange,
  onClear,
}: {
  row: SelectionRow;
  catalog: CourseCatalogItem[];
  selectedKeys: Set<string>;
  onChange: (course: CourseCatalogItem) => void;
  onClear: () => void;
}) {
  const baseId = useId();
  const inputId = `${baseId}-course`;
  const listboxId = `${baseId}-listbox`;
  const [query, setQuery] = useState(row.course_code ? courseLabel(row.course_code, row.course_name) : "");
  const [isOpen, setIsOpen] = useState(false);
  const [activeIndex, setActiveIndex] = useState(0);
  const committedCourseKey = useRef(row.courseKey);
  const options = matchingCourses(catalog, query, selectedKeys);

  useEffect(() => {
    if (committedCourseKey.current !== row.courseKey) {
      setQuery(row.course_code ? courseLabel(row.course_code, row.course_name) : "");
      committedCourseKey.current = row.courseKey;
    }
  }, [row.courseKey, row.course_code, row.course_name]);

  function choose(course: CourseCatalogItem) {
    onChange(course);
    committedCourseKey.current = course.key;
    setQuery(courseLabel(course.course_code, course.course_name));
    setIsOpen(false);
    setActiveIndex(0);
  }

  return (
    <div
      className="field combobox-field"
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget)) {
          setIsOpen(false);
        }
      }}
    >
      <label htmlFor={inputId}>{COPY.selection.courseLabel}</label>
      <input
        id={inputId}
        type="text"
        role="combobox"
        aria-autocomplete="list"
        aria-expanded={isOpen}
        aria-controls={listboxId}
        aria-activedescendant={isOpen && options[activeIndex] ? `${listboxId}-${activeIndex}` : undefined}
        placeholder={COPY.selection.coursePlaceholder}
        value={query}
        onFocus={() => setIsOpen(true)}
        onChange={(event) => {
          setQuery(event.target.value);
          if (row.courseKey) {
            committedCourseKey.current = "";
            onClear();
          }
          setIsOpen(true);
          setActiveIndex(0);
        }}
        onKeyDown={(event) => {
          if (!isOpen && (event.key === "ArrowDown" || event.key === "ArrowUp")) {
            setIsOpen(true);
            return;
          }
          if (event.key === "ArrowDown") {
            event.preventDefault();
            setActiveIndex((current) => Math.min(current + 1, Math.max(0, options.length - 1)));
          }
          if (event.key === "ArrowUp") {
            event.preventDefault();
            setActiveIndex((current) => Math.max(0, current - 1));
          }
          if (event.key === "Enter" && options[activeIndex]) {
            event.preventDefault();
            choose(options[activeIndex]);
          }
          if (event.key === "Escape") {
            setIsOpen(false);
          }
        }}
      />
      {isOpen ? (
        <div className="course-options" id={listboxId} role="listbox" aria-label={COPY.selection.courseResultsLabel}>
          {options.length > 0 ? (
            options.map((course, index) => (
              <button
                id={`${listboxId}-${index}`}
                className="course-option"
                type="button"
                role="option"
                aria-selected={index === activeIndex}
                tabIndex={-1}
                key={course.key}
                onMouseDown={(event) => event.preventDefault()}
                onClick={() => choose(course)}
              >
                <strong>{course.course_code}</strong>
                <span>{course.course_name}</span>
                <small>{COPY.selection.lecturerCount(course.lecturers.length)}</small>
              </button>
            ))
          ) : (
            <p className="course-option empty" role="status">
              {COPY.selection.noCourseResults}
            </p>
          )}
        </div>
      ) : null}
    </div>
  );
}

function SolvePanel({
  rows,
  assignments,
  timeoutSeconds,
  solveState,
  onTimeoutChange,
  onBack,
  onSolve,
  hasIncompleteRows,
  headingRef,
}: {
  rows: SelectionRow[];
  assignments: DesiredAssignmentPayload[];
  timeoutSeconds: number;
  solveState: SolveState;
  onTimeoutChange: (value: number) => void;
  onBack: () => void;
  onSolve: () => void;
  hasIncompleteRows: boolean;
  headingRef: RefObject<HTMLHeadingElement>;
}) {
  return (
    <section className="panel-grid" aria-labelledby="solve-heading">
      <div className="card primary-card">
        <p className="section-kicker">{COPY.solve.kicker}</p>
        <h2 id="solve-heading" ref={headingRef} tabIndex={-1}>{COPY.solve.heading}</h2>
        <p className="section-copy">{COPY.solve.description}</p>
        <p className="section-note">{COPY.solve.fixedScheduleNote}</p>

        <fieldset className="timeout-fieldset">
          <legend>{COPY.solve.timeoutLegend}</legend>
          <div className="preset-row">
            {TIMEOUT_PRESETS.map((preset) => (
              <button
                className="preset-chip"
                type="button"
                data-active={timeoutSeconds === preset.value || undefined}
                key={preset.value}
                onClick={() => onTimeoutChange(preset.value)}
              >
                <strong>{preset.label}</strong>
                <span>{preset.note}</span>
              </button>
            ))}
          </div>
          <label className="field inline-field">
            <span>{COPY.solve.customTimeout(TIMEOUT_MIN, TIMEOUT_MAX)}</span>
            <input
              type="number"
              min={TIMEOUT_MIN}
              max={TIMEOUT_MAX}
              value={timeoutSeconds}
              onChange={(event) => onTimeoutChange(Number(event.target.value))}
              onBlur={() => onTimeoutChange(clampTimeout(timeoutSeconds))}
            />
          </label>
        </fieldset>

        <div className="action-row split">
          <button className="button secondary" type="button" onClick={onBack}>{COPY.solve.back}</button>
          <button
            className="button primary"
            type="button"
            disabled={assignments.length === 0 || hasIncompleteRows || solveState === "busy"}
            onClick={onSolve}
          >
            {solveState === "busy" ? COPY.solve.busyStart : COPY.solve.start}
          </button>
        </div>
      </div>

      <aside className="card rail-card" aria-label={COPY.solve.summaryLabel}>
        <h3>{COPY.solve.summaryHeading(assignments.length, rows.length)}</h3>
        {assignments.length > 0 ? (
          <ul className="compact-list">
            {assignments.map((assignment) => (
               <li key={`${assignment.course_code}-${assignment.teaching_team_key}`}>
                 <strong>{assignment.course_code}</strong>
                 <span>{assignment.teaching_team_label}</span>
              </li>
            ))}
          </ul>
        ) : (
          <EmptyRail text={COPY.solve.emptySummary} />
        )}
      </aside>
    </section>
  );
}

function ResultsPanel({
  solveState,
  response,
  solutionIndex,
  elapsedMs,
  timeoutSeconds,
  onSolutionChange,
  onCancel,
  onExportUnsat,
  isExportingUnsat,
  onBackToSelection,
  onReset,
  headingRef,
}: {
  solveState: SolveState;
  response: RescheduleResponse | null;
  solutionIndex: number;
  elapsedMs: number;
  timeoutSeconds: number;
  onSolutionChange: (index: number) => void;
  onCancel: () => void;
  onExportUnsat: () => void;
  isExportingUnsat: boolean;
  onBackToSelection: () => void;
  onReset: () => void;
  headingRef: RefObject<HTMLHeadingElement>;
}) {
  if (solveState === "busy") {
    return (
      <section className="card primary-card solo-card" aria-labelledby="solving-heading">
        <p className="section-kicker">{COPY.solve.kicker}</p>
        <h2 id="solving-heading" ref={headingRef} tabIndex={-1}>{COPY.results.busyHeading}</h2>
        <p className="section-copy">{COPY.results.busyDetail}</p>
        <div className="progress-track" role="progressbar" aria-label={COPY.results.busyProgressLabel} aria-valuetext={COPY.results.busyProgressText}>
          <span />
        </div>
        <p className="timer-text">{COPY.results.elapsed(Math.round(elapsedMs / 1000), timeoutSeconds)}</p>
        <div className="action-row">
          <button className="button secondary" type="button" onClick={onCancel}>{COPY.solve.cancel}</button>
        </div>
      </section>
    );
  }

  if (!response) {
    return (
      <section className="card primary-card solo-card" aria-labelledby="no-result-heading">
        <p className="section-kicker">{COPY.results.kicker}</p>
        <h2 id="no-result-heading" ref={headingRef} tabIndex={-1}>{COPY.results.noResultHeading}</h2>
        <p className="section-copy">{COPY.results.noResultDetail}</p>
        <div className="action-row">
          <button className="button secondary" type="button" onClick={onBackToSelection}>{COPY.solve.back}</button>
        </div>
      </section>
    );
  }

  const currentSolution = response.solutions[solutionIndex] ?? response.solutions[0] ?? null;
  const isSat = response.solver.satisfiability === "SAT" && currentSolution;
  const isUnsat = response.solver.satisfiability === "UNSAT";
  const hasSolutionTabs = response.solutions.length > 1;

  return (
    <section className="results-layout" aria-labelledby="results-heading">
      <div className="card primary-card">
        <p className="section-kicker">{COPY.results.kicker}</p>
        <h2 id="results-heading" ref={headingRef} tabIndex={-1}>
          {isSat ? COPY.results.validHeading : response.solver.satisfiability === "UNSAT" ? COPY.results.noScheduleHeading : COPY.results.uncertainHeading}
        </h2>
        <SolverSummaryCards response={response} />

        {hasSolutionTabs ? (
          <SolutionTabs
            solutions={response.solutions}
            selectedIndex={solutionIndex}
            onChange={onSolutionChange}
          />
        ) : null}

        {currentSolution ? (
          <div
            role={hasSolutionTabs ? "tabpanel" : undefined}
            id={hasSolutionTabs ? `solution-panel-${currentSolution.solution_index}` : undefined}
            aria-labelledby={hasSolutionTabs ? `solution-tab-${currentSolution.solution_index}` : undefined}
            tabIndex={hasSolutionTabs ? 0 : undefined}
          >
            <OnlineStrip solution={currentSolution} />
            <WeekGrid solution={currentSolution} />
            <MobileAgenda solution={currentSolution} />
            <ResultDetails solution={currentSolution} />
          </div>
        ) : (
          <div className="empty-state">
            <strong>{COPY.results.emptyHeading}</strong>
            <p>{COPY.results.emptyDetail}</p>
            {isUnsat && isDesktopApp() && response.solver.formal_verification_token ? (
              <div className="action-row">
                <button
                  className="button secondary"
                  type="button"
                  disabled={isExportingUnsat}
                  onClick={onExportUnsat}
                >
                  {isExportingUnsat ? COPY.results.exportingUnsat : COPY.results.exportUnsat}
                </button>
              </div>
            ) : null}
            {isUnsat && response.parse_summary.partial_import ? (
              <p>{COPY.results.partialUnsatDetail}</p>
            ) : null}
          </div>
        )}

        <ResultNotes response={response} />
      </div>

      <aside className="card rail-card result-rail" aria-label={COPY.results.railLabel}>
        {currentSolution ? <SolutionRail solution={currentSolution} /> : <EmptyRail text={COPY.results.emptyRail} />}
        <div className="rail-actions">
          <button className="button secondary" type="button" onClick={onBackToSelection}>{COPY.results.changeSelection}</button>
          <button className="button ghost" type="button" onClick={onReset}>{COPY.results.chooseAnotherFile}</button>
        </div>
      </aside>
    </section>
  );
}

function SolverSummaryCards({ response }: { response: RescheduleResponse }) {
  const items = [
    [COPY.results.summaryStatus, response.solver.satisfiability === "SAT" ? "Có lịch" : response.solver.satisfiability === "UNSAT" ? "Không có lịch" : "Chưa xác định"],
    [COPY.results.summaryTime, formatMilliseconds(response.solver.solve_time_ms)],
    [COPY.results.summaryCount, response.solver.solution_count],
  ];

  return (
    <div className="metric-grid compact-metrics">
      {items.map(([label, value]) => (
        <div className="metric-card" key={label}>
          <span>{label}</span>
          <strong>{value}</strong>
        </div>
      ))}
    </div>
  );
}

function SolutionTabs({
  solutions,
  selectedIndex,
  onChange,
}: {
  solutions: RescheduleResponse["solutions"];
  selectedIndex: number;
  onChange: (index: number) => void;
}) {
  const tabRefs = useRef<Array<HTMLButtonElement | null>>([]);

  function moveTab(index: number) {
    const nextIndex = (index + solutions.length) % solutions.length;
    onChange(nextIndex);
    tabRefs.current[nextIndex]?.focus();
  }

  return (
    <div className="solution-tabs" role="tablist" aria-label={COPY.results.solutionsLabel}>
      {solutions.map((solution, index) => (
        <button
          ref={(element) => {
            tabRefs.current[index] = element;
          }}
          className="solution-tab"
          type="button"
          role="tab"
          id={`solution-tab-${solution.solution_index}`}
          aria-controls={`solution-panel-${solution.solution_index}`}
          aria-selected={selectedIndex === index}
          tabIndex={selectedIndex === index ? 0 : -1}
          key={solution.solution_index}
          onClick={() => onChange(index)}
          onKeyDown={(event) => {
            if (event.key === "ArrowRight") {
              event.preventDefault();
              moveTab(index + 1);
            }
            if (event.key === "ArrowLeft") {
              event.preventDefault();
              moveTab(index - 1);
            }
            if (event.key === "Home") {
              event.preventDefault();
              moveTab(0);
            }
            if (event.key === "End") {
              event.preventDefault();
              moveTab(solutions.length - 1);
            }
          }}
        >
          {COPY.results.solution(solution.solution_index)}
          <span>{COPY.results.movement(solution.movement_cost)}</span>
        </button>
      ))}
    </div>
  );
}

function OnlineStrip({ solution }: { solution: NonNullable<RescheduleResponse["solutions"][number]> }) {
  const sessions = onlineSessionsForSolution(solution);
  if (sessions.length === 0) {
    return null;
  }

  return (
    <section className="online-strip" aria-label={COPY.results.onlineLabel}>
      <strong>{COPY.results.online}</strong>
      <div>
        {sessions.map((session) => (
          <span className="online-pill" key={session.session_id}>
            {session.course_code} · {session.lhp_code}
          </span>
        ))}
      </div>
    </section>
  );
}

function WeekGrid({ solution }: { solution: NonNullable<RescheduleResponse["solutions"][number]> }) {
  const sessions = physicalSessionsForSolution(solution);

  return (
    <section className="week-section" aria-label={COPY.results.weekLabel}>
      <div className="week-grid" aria-label={COPY.results.weekGridLabel}>
          <div className="grid-corner">{COPY.results.weekPeriodLabel}</div>
        {DAYS.map((day) => (
          <div className="grid-header" key={day}>{DAY_LABELS.get(day)}</div>
        ))}
        {PERIODS.map((period, periodIndex) => (
          <div
            className="grid-period"
            key={period}
            style={{ gridColumn: "1", gridRow: String(periodIndex + 2) }}
          >
            <strong>{PERIOD_LABELS.get(period)?.label}</strong>
            <span>{PERIOD_LABELS.get(period)?.time}</span>
          </div>
        ))}
        {PERIODS.flatMap((period, periodIndex) =>
          DAYS.map((day, dayIndex) => (
            <div
              className="grid-cell"
              aria-hidden="true"
              key={`${day}-${period}`}
              style={{ gridColumn: String(dayIndex + 2), gridRow: String(periodIndex + 2) }}
            />
          )),
        )}
        {sessions.map((session) => {
          const { start, span } = periodSpan(session);
          const column = DAYS.indexOf(session.day as (typeof DAYS)[number]) + 2;
          const row = PERIODS.indexOf(start as (typeof PERIODS)[number]) + 2;
          if (column < 2 || row < 2) {
            return null;
          }
          return (
            <button
              className="session-block"
              type="button"
              key={session.session_id}
              style={{ gridColumn: `${column} / span 1`, gridRow: `${row} / span ${span}` }}
               aria-label={`${session.course_code}, ${session.lhp_code}, ${session.timeslot_label}, ${COPY.results.roomLabel} ${session.room_code}`}
              onClick={() => {
                const target = document.getElementById(sessionDetailId(session.session_id));
                target?.scrollIntoView({ block: "center", behavior: "smooth" });
                target?.focus({ preventScroll: true });
              }}
            >
              <strong>{session.course_code}</strong>
              <span>{session.lhp_code}</span>
              <small>{session.room_code}</small>
            </button>
          );
        })}
      </div>
    </section>
  );
}

function MobileAgenda({ solution }: { solution: NonNullable<RescheduleResponse["solutions"][number]> }) {
  const sessions = physicalSessionsForSolution(solution);
  if (sessions.length === 0) {
    return null;
  }

  return (
    <section className="mobile-agenda" aria-label={COPY.results.mobileLabel}>
      <h3>{COPY.results.mobileLabel}</h3>
      {DAYS.map((day) => {
        const daySessions = sessions.filter((session) => session.day === day);
        if (daySessions.length === 0) {
          return null;
        }
        return (
          <div className="agenda-day" key={day}>
            <strong>{DAY_LABELS.get(day)}</strong>
            {daySessions.map((session) => (
              <div className="agenda-item" key={session.session_id}>
                <span>{session.timeslot_label}</span>
                <strong>{session.course_code} · {session.lhp_code}</strong>
                <small>{session.room_code}</small>
              </div>
            ))}
          </div>
        );
      })}
    </section>
  );
}

function ResultDetails({ solution }: { solution: NonNullable<RescheduleResponse["solutions"][number]> }) {
  return (
    <section className="details-section" aria-label={COPY.results.detailLabel}>
      <h3>{COPY.results.detailHeading}</h3>
      {solution.desired_assignments.map((anchor) => (
         <article className="detail-card" key={`${anchor.course_code}-${anchor.teaching_team_key}`}>
          <header>
            <div>
              <strong>{anchor.course_code}</strong>
              <span>{anchor.course_name}</span>
            </div>
             <p>{anchor.teaching_team_label}</p>
          </header>
          {normalizedLhpSchedules(anchor).map((lhp) => (
            <div className="lhp-group" key={lhp.lhp_code}>
              <h4>{lhp.lhp_code}</h4>
              <ul>
                {lhp.matched_sessions.map((session) => (
                  <li id={sessionDetailId(session.session_id)} tabIndex={-1} key={session.session_id}>
                    <span className="session-type">{session.session_type}</span>
                    <div>
                      <strong>{session.timeslot_label}</strong>
                      <span>{session.room_code} · {compactList(session.cohort_codes)}</span>
                    </div>
                  </li>
                ))}
              </ul>
            </div>
          ))}
        </article>
      ))}
    </section>
  );
}

function ResultNotes({ response }: { response: RescheduleResponse }) {
  const notes = [
    ...response.solver.explanation,
    ...(response.solved_schedule_validation?.sample_violations ?? []),
  ];
  const visibleNotes = notes.map(userFacingNote).filter((note): note is string => note !== null);
  if (visibleNotes.length === 0) {
    return null;
  }

  return (
    <section className="notes-panel" aria-label={COPY.results.notesLabel}>
      <h3>{COPY.results.notesLabel}</h3>
      <ul>
        {visibleNotes.slice(0, 8).map((note) => (
          <li key={note}>{note}</li>
        ))}
      </ul>
    </section>
  );
}

function SolutionRail({ solution }: { solution: NonNullable<RescheduleResponse["solutions"][number]> }) {
  const stats = solutionStats(solution);
  return (
    <div className="solution-rail-content">
      <span className="rail-label">{COPY.results.movementLabel}</span>
      <strong className="movement-cost">{solution.movement_cost}</strong>
      <div className="rail-stats">
        <span>{stats.lhpCount} LHP</span>
        <span>{stats.sessionCount} buổi</span>
      </div>
    </div>
  );
}

function EmptyRail({ text }: { text: string }) {
  return <p className="empty-rail">{text}</p>;
}

export default App;
