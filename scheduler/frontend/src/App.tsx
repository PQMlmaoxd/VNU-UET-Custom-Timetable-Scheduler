import { useEffect, useMemo, useRef, useState } from "react";

import {
  exportUnsatArtifact,
  isDesktopApp,
  notifyDesktopReady,
  setDesktopTheme,
  solveWorkbook,
  validateWorkbook,
} from "./api";
import { COPY, userFacingError } from "./copy";
import { AppShell } from "./components/AppShell";
import { WorkflowNavigation } from "./components/WorkflowNavigation";
import { ResultsPanel } from "./features/results/ResultsPanel";
import { SelectionPanel } from "./features/selection/SelectionPanel";
import { SolvePanel } from "./features/solve/SolvePanel";
import { UploadPanel } from "./features/upload/UploadPanel";
import {
  applyTheme,
  readThemePreference,
  saveThemePreference,
  type ThemePreference,
} from "./theme";
import type {
  CourseCatalogItem,
  RescheduleResponse,
  SelectionRow,
  SolveState,
  StepId,
  ValidateExistingResponse,
} from "./types";
import {
  TIMEOUT_DEFAULT,
  buildCourseCatalog,
  clampTimeout,
  hasIncompleteSelections,
  isSupportedTimetableFile,
  selectedAssignments,
} from "./utils";
import type { AppStatus } from "./components/Notice";

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
    <AppShell
      file={file}
      themePreference={themePreference}
      status={status}
      onThemeChange={changeTheme}
    >
      <WorkflowNavigation
        currentStep={clampedCurrentStep}
        maxStep={maxStep}
        onStepChange={setCurrentStep}
      />

      <div id="workspace" className="workspace" tabIndex={-1}>
        {clampedCurrentStep === 1 ? (
          <UploadPanel
            file={file}
            isLoading={isLoadingWorkbook}
            onFileChange={acceptFile}
            onLoad={loadWorkbook}
            headingRef={stageHeading}
          />
        ) : null}

        {clampedCurrentStep === 2 && workbook ? (
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

        {clampedCurrentStep === 3 && workbook && solveState !== "busy" && !solveResponse ? (
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

        {clampedCurrentStep === 3 && (solveState === "busy" || solveResponse || solveState === "error") ? (
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
    </AppShell>
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

export default App;
