import { useEffect, useId, useRef, useState, type RefObject } from "react";

import { COPY } from "../../copy";
import type {
  CourseCatalogItem,
  DesiredAssignmentPayload,
  SelectionRow,
  ValidateExistingResponse,
} from "../../types";
import { courseLabel, matchingCourses } from "../../utils";
import { EmptyRail } from "../shared/EmptyRail";

type SelectionPanelProps = {
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
  onLecturerChange: (rowId: string, teachingTeamKey: string) => void;
  onAdd: () => void;
  onRemove: (rowId: string) => void;
  hasIncompleteRows: boolean;
  onContinue: () => void;
  headingRef: RefObject<HTMLHeadingElement>;
};

export function SelectionPanel({
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
}: SelectionPanelProps) {
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
              <fieldset className="selection-row" key={row.id}>
                <legend className="sr-only">{COPY.selection.rowLabel(index + 1)}</legend>
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
              </fieldset>
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
