import { useRef, type RefObject } from "react";

import { isDesktopApp } from "../../api";
import { COPY, userFacingNote } from "../../copy";
import type { RescheduleResponse, SolveState } from "../../types";
import {
  DAYS,
  DAY_LABELS,
  PERIODS,
  PERIOD_LABELS,
  compactList,
  formatMilliseconds,
  normalizedLhpSchedules,
  onlineSessionsForSolution,
  periodSpan,
  physicalSessionsForSolution,
  sessionDetailId,
} from "../../utils";

type ResultsPanelProps = {
  solveState: SolveState;
  response: RescheduleResponse | null;
  solutionIndex: number;
  elapsedMs: number;
  timeoutSeconds: number;
  onSolutionChange: (index: number) => void;
  onCancel: () => void;
  onExportUnsat: () => void;
  isExportingUnsat: boolean;
  onSaveTimetable: () => void;
  isSavingTimetable: boolean;
  isLoadedDocument: boolean;
  hasLockedSelections: boolean;
  onBackToSelection: () => void;
  onReset: () => void;
  headingRef: RefObject<HTMLHeadingElement>;
};

type Solution = NonNullable<RescheduleResponse["solutions"][number]>;

export function ResultsPanel({
  solveState,
  response,
  solutionIndex,
  elapsedMs,
  timeoutSeconds,
  onSolutionChange,
  onCancel,
  onExportUnsat,
  isExportingUnsat,
  onSaveTimetable,
  isSavingTimetable,
  isLoadedDocument,
  hasLockedSelections,
  onBackToSelection,
  onReset,
  headingRef,
}: ResultsPanelProps) {
  if (solveState === "busy") {
    return (
      <section className="panel panel-pad solo-panel" aria-labelledby="solving-heading">
        <h2 id="solving-heading" ref={headingRef} tabIndex={-1}>{COPY.results.busyHeading}</h2>
        <p className="stage-copy">{COPY.results.busyDetail}</p>
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
      <section className="panel panel-pad solo-panel" aria-labelledby="no-result-heading">
        <h2 id="no-result-heading" ref={headingRef} tabIndex={-1}>{COPY.results.noResultHeading}</h2>
        <p className="stage-copy">{COPY.results.noResultDetail}</p>
        <div className="action-row">
          <button className="button secondary" type="button" onClick={onBackToSelection}>{COPY.solve.back}</button>
        </div>
      </section>
    );
  }

  const currentSolution = response.solutions[solutionIndex] ?? response.solutions[0] ?? null;
  const isSat = response.solver.satisfiability === "SAT" && currentSolution !== null;
  const isUnsat = response.solver.satisfiability === "UNSAT";
  const hasSolutionTabs = response.solutions.length > 1;

  const heading = isSat
    ? COPY.results.headingSat
    : isUnsat
      ? COPY.results.headingUnsat
      : COPY.results.headingUnknown;

  const statusWord = response.solver.satisfiability === "SAT"
    ? "Có lịch"
    : response.solver.satisfiability === "UNSAT"
      ? "Không có lịch"
      : "Chưa xác định";

  return (
    <section aria-labelledby="results-heading">
      <div className="panel panel-pad">
        <div className="results-head">
          <h2 id="results-heading" ref={headingRef} tabIndex={-1}>{heading}</h2>
          <div className="result-actions">
            {isSat && isDesktopApp() ? (
              <button className="button primary compact" type="button" disabled={isSavingTimetable} onClick={onSaveTimetable}>
                {isSavingTimetable ? COPY.results.savingTimetable : COPY.results.saveTimetable}
              </button>
            ) : null}
            {!isLoadedDocument ? (
              <button className="button secondary compact" type="button" onClick={onBackToSelection}>{COPY.results.changeSelection}</button>
            ) : null}
            <button className="button ghost compact" type="button" onClick={onReset}>{COPY.results.chooseAnotherFile}</button>
          </div>
        </div>

        <p className="result-meta">
          <span>{`${COPY.results.statusMeta}: ${statusWord}`}</span>
          <span>{`${COPY.results.timeMeta}: ${formatMilliseconds(response.solver.solve_time_ms)}`}</span>
          <span>{`${COPY.results.countMeta}: ${response.solver.solution_count}`}</span>
        </p>

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
            <OnlineNote solution={currentSolution} />
            <WeekGrid solution={currentSolution} />
            <Agenda solution={currentSolution} />
            {response.solutions.length > 1 ? (
              <p className="stage-note">{COPY.results.movementHint}</p>
            ) : null}
            <ResultDetails solution={currentSolution} />
          </div>
        ) : (
          <EmptyResult
            response={response}
            isUnsat={isUnsat}
            hasLockedSelections={hasLockedSelections}
            onExportUnsat={onExportUnsat}
            isExportingUnsat={isExportingUnsat}
          />
        )}

        <ResultNotes response={response} isLoadedDocument={isLoadedDocument} />
      </div>
    </section>
  );
}

function EmptyResult({
  response,
  isUnsat,
  hasLockedSelections,
  onExportUnsat,
  isExportingUnsat,
}: {
  response: RescheduleResponse;
  isUnsat: boolean;
  hasLockedSelections: boolean;
  onExportUnsat: () => void;
  isExportingUnsat: boolean;
}) {
  return (
    <div className="empty-state">
      <strong>{hasLockedSelections && isUnsat ? COPY.results.unsatLockedHeading : COPY.results.emptyHeading}</strong>
      {response.solver.status === "timeout" ? (
        <p>{COPY.results.timeoutDetail}</p>
      ) : (
        <>
          {isUnsat && hasLockedSelections ? <p>{COPY.results.unsatLockedDetail}</p> : <p>{COPY.results.emptyDetail}</p>}
          {isUnsat ? (
            <ul
              className="guidance-list"
              aria-label={COPY.results.unsatGuidanceTitle}
            >
              {(hasLockedSelections ? COPY.results.unsatLockGuidance : COPY.results.unsatGuidance).map((item) => (
                <li key={item}>{item}</li>
              ))}
            </ul>
          ) : null}
        </>
      )}
      {isUnsat && isDesktopApp() && response.solver.formal_verification_token ? (
        <button
          className="button secondary"
          type="button"
          disabled={isExportingUnsat}
          onClick={onExportUnsat}
        >
          {isExportingUnsat ? COPY.results.exportingUnsat : COPY.results.exportUnsat}
        </button>
      ) : null}
      {isUnsat && response.parse_summary.partial_import ? (
        <p>{COPY.results.partialUnsatDetail}</p>
      ) : null}
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

function OnlineNote({ solution }: { solution: Solution }) {
  const sessions = onlineSessionsForSolution(solution);
  if (sessions.length === 0) {
    return null;
  }

  return (
    <p className="online-note" aria-label={COPY.results.onlineLabel}>
      <strong>{COPY.results.online}: </strong>
      {sessions.map((session) => (
        <span key={session.session_id}>
          {session.course_code} · {session.lhp_code}
        </span>
      ))}
    </p>
  );
}

function WeekGrid({ solution }: { solution: Solution }) {
  const sessions = physicalSessionsForSolution(solution);

  function focusSession(sessionId: string) {
    const target = document.getElementById(sessionDetailId(sessionId));
    const prefersReducedMotion = typeof window.matchMedia === "function"
      && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    target?.scrollIntoView({ block: "center", behavior: prefersReducedMotion ? "auto" : "smooth" });
    target?.focus({ preventScroll: true });
  }

  const covered = new Set<string>();
  for (const session of sessions) {
    const { start, span } = periodSpan(session);
    const startIndex = PERIODS.indexOf(start as (typeof PERIODS)[number]);
    for (let offset = 1; offset < Math.max(1, span); offset += 1) {
      const followingPeriod = PERIODS[startIndex + offset];
      if (followingPeriod) {
        covered.add(`${session.day}|${followingPeriod}`);
      }
    }
  }

  return (
    <section className="week-section" aria-label={COPY.results.weekLabel}>
      <div className="week-scroll">
        <table className="week-table" aria-label={COPY.results.weekGridLabel}>
          <caption className="sr-only">{COPY.results.weekLabel}</caption>
          <thead>
            <tr>
              <th scope="col">{COPY.results.weekPeriodLabel}</th>
              {DAYS.map((day) => (
                <th scope="col" key={day}>{DAY_LABELS.get(day)}</th>
              ))}
            </tr>
          </thead>
          <tbody>
            {PERIODS.map((period, periodIndex) => (
              <tr key={period}>
                <th scope="row" className="period-cell">
                  <strong>{PERIOD_LABELS.get(period)?.label}</strong>
                  <span>{PERIOD_LABELS.get(period)?.time}</span>
                </th>
                {DAYS.map((day) => {
                  const session = sessions.find(
                    (candidate) =>
                      candidate.day === day
                      && periodSpan(candidate).start === period,
                  );
                  if (!session) {
                    if (covered.has(`${day}|${period}`)) {
                      return null;
                    }
                    return <td className="cell-empty" key={`${day}-${period}`} />;
                  }

                  const { span } = periodSpan(session);
                  return (
                    <td key={`${day}-${period}`} rowSpan={Math.max(1, span)}>
                      <button
                        className="session-block"
                        type="button"
                        aria-label={`${session.course_code}, ${session.lhp_code}, ${session.timeslot_label}, ${COPY.results.roomLabel} ${session.room_code}`}
                        onClick={() => focusSession(session.session_id)}
                      >
                        <strong>{session.course_code}</strong>
                        <span>{session.lhp_code}</span>
                        <small>{session.room_code}</small>
                      </button>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function Agenda({ solution }: { solution: Solution }) {
  const sessions = physicalSessionsForSolution(solution);
  if (sessions.length === 0) {
    return null;
  }

  return (
    <details className="agenda" open aria-label={COPY.results.agendaLabel}>
      <summary>{COPY.results.agendaLabel}</summary>
      <div className="agenda-body">
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
      </div>
    </details>
  );
}

function ResultDetails({ solution }: { solution: Solution }) {
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
            <div className="lhp-block" key={lhp.lhp_code}>
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

function ResultNotes({
  response,
  isLoadedDocument,
}: {
  response: RescheduleResponse;
  isLoadedDocument: boolean;
}) {
  const notes = [
    ...response.solver.explanation,
    ...(response.solved_schedule_validation?.sample_violations ?? []),
  ];
  const visibleNotes = [
    ...(isLoadedDocument ? [COPY.results.loadedJsonNonAuthoritativeDetail] : []),
    ...(response.parse_summary.partial_import && response.solver.satisfiability !== "UNSAT"
      ? [COPY.results.partialImportDetail(response.parse_summary.quarantined_lhp_count)]
      : []),
    ...notes.map(userFacingNote).filter((note): note is string => note !== null),
  ];
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
