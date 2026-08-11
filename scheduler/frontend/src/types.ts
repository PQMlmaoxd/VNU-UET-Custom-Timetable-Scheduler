export type ParseSummary = {
  sessions: number;
  schedulable_sessions: number;
  online_sessions: number;
  other_department_sessions: number;
  lecturer_blocks: number;
  anchor_count: number;
  requested_assignments: number;
  rooms: number;
  skipped_rows: number;
  fatal_warning_count: number;
  warnings: string[];
};

export type AnchorCatalogItem = {
  course_code: string;
  course_name: string;
  teaching_team_key: string;
  teaching_team_label: string;
  session_count: number;
};

export type RoomCostRuleItem = {
  from_zone: string;
  to_zone: string;
  cost: number;
  description: string;
};

export type PrototypeCatalog = {
  anchors: AnchorCatalogItem[];
  room_cost_rules: RoomCostRuleItem[];
};

export type ValidationSummary = {
  is_valid: boolean;
  is_complete: boolean;
  violation_count: number;
  missing_session_count: number;
  sample_violations: string[];
};

export type SessionScheduleItem = {
  session_id: string;
  course_code: string;
  course_name: string;
  lhp_code: string;
  session_type: string;
  lecturer_names: string[];
  cohort_codes: string[];
  timeslot_label: string;
  room_code: string;
  source_row: number;
  day: number | null;
  period_code: string | null;
  period_atomic: string[] | null;
};

export type LhpScheduleItem = {
  lhp_code: string;
  session_count: number;
  matched_sessions: SessionScheduleItem[];
};

export type DesiredAnchorSummary = {
  course_code: string;
  teaching_team_key: string;
  teaching_team_label: string;
  course_name: string;
  lhp_codes: string[];
  session_count: number;
  matched_sessions: SessionScheduleItem[];
  lhp_schedules: LhpScheduleItem[];
};

export type SolverSummary = {
  backend: string;
  status: string;
  satisfiability: string;
  solve_time_ms: number;
  objective_value: number | null;
  assignment_count: number;
  solution_count: number;
  solver_info: string;
  explanation: string[];
  formal_verification_token: string | null;
};

export type SelectedSolutionItem = {
  solution_index: number;
  movement_cost: number;
  desired_assignments: DesiredAnchorSummary[];
};

export type ValidateExistingResponse = {
  mode: string;
  workbook_path: string;
  department: string;
  parse_summary: ParseSummary;
  prototype_catalog: PrototypeCatalog;
  existing_schedule_validation: ValidationSummary;
};

export type RescheduleResponse = {
  mode: string;
  workbook_path: string;
  department: string;
  parse_summary: ParseSummary;
  prototype_catalog: PrototypeCatalog;
  desired_assignments: DesiredAnchorSummary[];
  solutions: SelectedSolutionItem[];
  existing_schedule_validation: ValidationSummary;
  solver: SolverSummary;
  solved_schedule_validation: ValidationSummary | null;
};

export type CourseCatalogItem = {
  key: string;
  course_code: string;
  course_name: string;
  lecturers: AnchorCatalogItem[];
  searchText: string;
};

export type SelectionRow = {
  id: string;
  courseKey: string;
  course_code: string;
  course_name: string;
  teaching_team_key: string;
  teaching_team_label: string;
};

export type DesiredAssignmentPayload = {
  course_code: string;
  course_name: string;
  teaching_team_key: string;
  teaching_team_label: string;
};

export type StatusTone = "idle" | "busy" | "success" | "warning" | "error";

export type StepId = 1 | 2 | 3;
