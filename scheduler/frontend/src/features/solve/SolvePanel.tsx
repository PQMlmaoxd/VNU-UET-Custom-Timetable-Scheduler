import type { RefObject } from "react";

import { COPY } from "../../copy";
import type { DesiredAssignmentPayload, SelectionRow, SolveState } from "../../types";
import { clampTimeout, TIMEOUT_MAX, TIMEOUT_MIN } from "../../utils";
import { EmptyRail } from "../shared/EmptyRail";

const TIMEOUT_PRESETS = [
  { label: COPY.solve.presets.quick, value: 30, note: COPY.solve.presets.quickNote },
  { label: COPY.solve.presets.balanced, value: 180, note: COPY.solve.presets.balancedNote },
  { label: COPY.solve.presets.thorough, value: 300, note: COPY.solve.presets.thoroughNote },
];

type SolvePanelProps = {
  rows: SelectionRow[];
  assignments: DesiredAssignmentPayload[];
  timeoutSeconds: number;
  solveState: SolveState;
  onTimeoutChange: (value: number) => void;
  onBack: () => void;
  onSolve: () => void;
  hasIncompleteRows: boolean;
  headingRef: RefObject<HTMLHeadingElement>;
};

export function SolvePanel({
  rows,
  assignments,
  timeoutSeconds,
  solveState,
  onTimeoutChange,
  onBack,
  onSolve,
  hasIncompleteRows,
  headingRef,
}: SolvePanelProps) {
  return (
    <section className="panel-grid" aria-labelledby="solve-heading">
      <div className="card primary-card">
        <p className="section-kicker">{COPY.solve.kicker}</p>
        <h2 id="solve-heading" ref={headingRef} tabIndex={-1}>{COPY.solve.heading}</h2>
        <p className="section-copy">{COPY.solve.description}</p>
        <p className="section-note">{COPY.solve.fixedScheduleNote}</p>

        <fieldset className="timeout-fieldset">
          <legend>{COPY.solve.timeoutLegend}</legend>
          <div className="preset-row" role="radiogroup" aria-label={COPY.solve.timeoutLegend}>
            {TIMEOUT_PRESETS.map((preset) => (
              <label className="preset-option" data-active={timeoutSeconds === preset.value || undefined} key={preset.value}>
                <input
                  className="sr-only"
                  type="radio"
                  name="solve-timeout-preset"
                  value={preset.value}
                  checked={timeoutSeconds === preset.value}
                  onChange={() => onTimeoutChange(preset.value)}
                />
                <span className="preset-option-copy">
                  <strong>{preset.label}</strong>
                  <span>{preset.note}</span>
                </span>
              </label>
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
