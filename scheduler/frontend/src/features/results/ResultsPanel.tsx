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
  solutionStats,
} from "../../utils";
import { EmptyRail } from "../shared/EmptyRail";

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
  onBackToSelection: () => void;
  onReset: () => void;
  headingRef: RefObject<HTMLHeadingElement>;
};

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
  onBackToSelection,
  onReset,
  headingRef,
}: ResultsPanelProps) {
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
          <span className="online-item" key={session.session_id}>
            {session.course_code} · {session.lhp_code}
          </span>
        ))}
      </div>
    </section>
  );
}

function WeekGrid({ solution }: { solution: NonNullable<RescheduleResponse["solutions"][number]> }) {
  const sessions = physicalSessionsForSolution(solution);

  function focusSession(sessionId: string) {
    const target = document.getElementById(sessionDetailId(sessionId));
    const prefersReducedMotion = typeof window.matchMedia === "function"
      && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    target?.scrollIntoView({ block: "center", behavior: prefersReducedMotion ? "auto" : "smooth" });
    target?.focus({ preventScroll: true });
  }

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
              onClick={() => focusSession(session.session_id)}
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
        <span className="rail-stat">{stats.lhpCount} LHP</span>
        <span className="rail-stat">{stats.sessionCount} buổi</span>
      </div>
    </div>
  );
}
