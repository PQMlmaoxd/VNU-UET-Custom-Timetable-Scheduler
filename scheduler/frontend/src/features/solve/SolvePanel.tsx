import type { RefObject } from "react";

import { COPY } from "../../copy";
import type { DesiredAssignmentPayload, SelectionRow, SolveState } from "../../types";
import { clampTimeout, TIMEOUT_MAX, TIMEOUT_MIN } from "../../utils";

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
    <section aria-labelledby="solve-heading">
      <div className="panel panel-pad">
        <h2 id="solve-heading" ref={headingRef} tabIndex={-1}>{COPY.solve.heading}</h2>
        <p className="stage-copy">{COPY.solve.description}</p>

        <fieldset className="timeout-group">
          <legend>{COPY.solve.timeoutLegend}</legend>
          <div className="segmented" role="radiogroup" aria-label={COPY.solve.timeoutLegend}>
            {TIMEOUT_PRESETS.map((preset) => (
              <label className="segment" data-active={timeoutSeconds === preset.value || undefined} key={preset.value}>
                <input
                  className="sr-only"
                  type="radio"
                  name="solve-timeout-preset"
                  value={preset.value}
                  checked={timeoutSeconds === preset.value}
                  onChange={() => onTimeoutChange(preset.value)}
                />
                <strong>{preset.label}</strong>
                <span>{preset.note}</span>
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

        <p className="stage-note">{COPY.solve.fixedScheduleNote}</p>

        <div className="summary-strip" aria-label={COPY.solve.summaryLabel}>
          <span>{rows.length > 0 ? `${assignments.length}/${rows.length}` : "0"}</span>
          {assignments.map((assignment) => (
            <span className="chip" key={`${assignment.course_code}-${assignment.teaching_team_key}`}>
              {assignment.course_code} · {assignment.teaching_team_label}
            </span>
          ))}
          {assignments.length === 0 ? <span>{COPY.solve.emptySummary}</span> : null}
        </div>

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
    </section>
  );
}
