# Hard Constraint Specification
## Fixed-Workbook Personal Timetable Selection

**Version:** 1.3 (Personal Selector)
**Status:** Authoritative for current product flow.
**Scope:** C# desktop personal-selection flow over a fixed timetable document.

---

## 1) Problem Model

The current product solves this problem only:

- Input is a fixed timetable workbook (sessions already have timeslot and room semantics).
- User sends a set of distinct selected teaching-team requests: `(course_code, teaching_team_key)`.
- For each selected teaching team, the system chooses exactly one valid teaching-unit candidate.
- Chosen `LHP` candidates must be pairwise non-overlapping by timeslot semantics.
- The desktop command returns up to `k` distinct solutions (`k=5` by default), then ranks them by movement cost.

This is **not** a global re-scheduling problem.

Duplicate `(course_code, teaching_team_key)` requests are invalid input and are rejected before candidate construction or CNF encoding.

---

## 2) Constraint Layers

There are two constraint layers in the application:

- **Layer A (Workbook integrity):** `HC-1..HC-6`
  - Used to validate the timetable itself (existing assignments).
  - Implemented by `TimetableValidator.Validate(...)`.
- **Layer B (Personal selector correctness):** `PT-1..PT-7`
  - Used to validate each personal selection solution.
  - Implemented by `TimetableValidator.ValidatePersonalTimetable(...)`.

Both layers are hard constraints in their own context.

---

## 3) Definitions

- **Session**: atomic teaching unit (one workbook row), with `session_id`, `lhp_code`, `course`, `session_type`, `lecturers`, `student_cohorts`, `timeslot`, `room`.
- **Schedulable session**: `session_type != ONL`.
- **Online session**: `session_type == ONL`.
- **Session-type normalization**: input templates may use `BT` and `LT+BT`. They are preserved as physical exercise session types and are not laboratory types; only `TH` and `TL+TH` require a laboratory room under HC-6.
- **Teaching team**: sorted individual lecturer names across one teaching unit. Organizational lecturers are not selectable team members.
- **Teaching-team request**: one user-selected `(course_code, teaching_team_key)` with a display label.
- **Teaching unit**: sessions with the same `course_code` and `lhp_code` that are transitively connected by cohort codes. A shared lecture for K1+K2 therefore joins K1 and K2 exercise sessions; disconnected cohort components remain separate units.
- **Candidate LHP** for a teaching-team request: one teaching unit, identified by `lhp_code` and `teaching_unit_key`, containing every session in that unit.
- **Personal choice**: one selected candidate for one teaching-team request.
- **Individual lecturer**: lecturer with `lecturer_type == individual`.
- **Virtual room**: room code in `VIRTUAL_ROOMS` (includes `ONL` and organizational/virtual locations).

### Timeslot overlap

Each period is mapped to atomic blocks:

- `"1" -> {1}`, `"2" -> {2}`, `"3" -> {3}`, `"4" -> {4}`
- `"1-2" -> {1,2}`, `"2-3" -> {2,3}`, `"3-4" -> {3,4}`
- `"Sáng" -> {1,2}`, `"Chiều" -> {3,4}`

`Overlap(t1, t2)` is true iff:

- `t1.day == t2.day`, and
- `AtomicSlots(t1.period) intersect AtomicSlots(t2.period) != empty`.

---

## 4) Layer A: Workbook Integrity Constraints (`HC-*`)

These constraints are used to validate a concrete schedule over workbook sessions.

### HC-1: No room double-booking (physical rooms)

At overlapping times, a physical room can host at most one session.

Formal:
```text
forall a1, a2 in Assignments:
  a1.session_id != a2.session_id
  and a1.room is physical
  and a1.room == a2.room
  => not Overlap(a1.timeslot, a2.timeslot)
```

Notes:

- Virtual rooms are excluded from HC-1.

### HC-2: No lecturer double-booking (individual lecturers)

An individual lecturer cannot teach overlapping sessions.

Formal:
```text
forall assigned sessions s1, s2 with assignments a1, a2:
  s1.session_id != s2.session_id
  and Overlap(a1.timeslot, a2.timeslot)
  => IndividualLecturers(s1) intersect IndividualLecturers(s2) == empty
```

Notes:

- Organizational units are excluded.
- Fixed online sessions with a concrete timeslot are included in overlap checks.

### HC-3: No cohort double-booking

A cohort cannot attend overlapping sessions.

Formal:
```text
forall assigned sessions s1, s2 with assignments a1, a2:
  s1.session_id != s2.session_id
  and Overlap(a1.timeslot, a2.timeslot)
  => Cohorts(s1) intersect Cohorts(s2) == empty
```

Notes:

- Combined cohort cells are split during parsing; this check is on atomic cohort codes.
- Fixed online sessions with a concrete timeslot are included in overlap checks.

### HC-4: Lecturer external availability

An individual lecturer cannot be assigned in a slot blocked by external-department teaching.

Formal:
```text
forall assignment a of session s:
  forall l in IndividualLecturers(s):
    not exists block in LecturerBlocks:
      block.lecturer == l
      and Overlap(a.timeslot, block.blocked_timeslot)
```

Implementation note:

- In current validator code, HC-4 is applied over `schedule.assignments` (schedulable assignments path).

### HC-5: LHP internal non-conflict

Sessions of the same `LHP` that share at least one cohort must not overlap.

Formal:
```text
forall assigned sessions s1, s2 with assignments a1, a2:
  s1.session_id != s2.session_id
  and s1.lhp_code == s2.lhp_code
  and Cohorts(s1) intersect Cohorts(s2) != empty
  => not Overlap(a1.timeslot, a2.timeslot)
```

### HC-6: Room compatibility

Lab-like sessions must be assigned to lab-capable rooms.

Formal:
```text
forall assignment a of session s:
  if s.session_type in {TH, TL+TH} then a.room.is_lab == true
```

---

## 5) Layer B: Personal Selector Constraints (`PT-*`)

These constraints define correctness for the personal timetable selection result.

### PT-1: Exactly one teaching unit per selected request

For each requested `(course_code, teaching_team_key)`, exactly one choice must be returned.

### PT-2: Chosen LHP must be in candidate set

The selected `(lhp_code, teaching_unit_key)` must belong to that request's candidate list.

### PT-3: Chosen session sequence must match candidate session sequence

For the chosen `lhp_code`, returned `session_ids` must exactly equal the candidate's
declared `session_ids` in canonical order: ascending `source_row`, then ordinal
`session_id`. The sequence requirement makes serialized results deterministic.

### PT-4: Chosen timeslots must match the fixed schedule

Returned `session_timeslots` must match resolved timeslots from:

- assignment timeslots for schedulable sessions, or
- fixed online session timeslots when present.

### PT-5: No session reuse across requests

The same `session_id` cannot be reused by two different selected teaching-team requests.

### PT-6: No overlap between chosen LHPs

Any timeslot from one chosen LHP must be non-overlapping with every timeslot from every other chosen LHP.

### PT-7: No unexpected requests in output

The solution cannot contain a teaching-team request that was not requested by the user.

---

## 6) SAT Encoding for Personal Selector

Let `x(i,j)` mean candidate `j` is selected for teaching-team request `i`.

### Exact-one per teaching-team request

For each teaching-team request `i`:

```text
(x(i,1) or x(i,2) or ... or x(i,m_i))
```

For each `j < k` in the same request:

```text
(not x(i,j) or not x(i,k))
```

### Cross-request conflict clauses

For candidates `c(i,j)` and `c(u,v)` where `i != u`, add:

```text
(not x(i,j) or not x(u,v))
```

iff either:

- they share at least one `session_id`, or
- at least one timeslot in `c(i,j)` overlaps at least one timeslot in `c(u,v)`.

### Top-k distinct enumeration

Each selected teaching-team request has exactly one positive candidate variable. After each SAT model,
let `M+` contain precisely those positive candidate variables, one from each selected
pair. Add the blocking clause:

```text
(not v1 or not v2 or ... or not vn)   for all v in M+
```

This blocks the returned personal timetable while allowing SAT auxiliary variables or
unselected candidates to take different values. It is equivalent to blocking the
complete model under the exact-one constraints, but its length is the number of
selected teaching-team requests rather than the total candidate-variable count.

Repeat until:

- `k` solutions collected, or
- UNSAT, or
- timeout.

---

## 7) Ranking Objective (Soft)

After hard-feasible personal solutions are found, rank by total movement cost.

Only consecutive selected sessions on the same day contribute cost.

If previous selected session ends at atomic period `p_end` and next selected session starts at `p_start`, cost applies only when:

```text
p_end + 1 == p_start
```

Transition cost:

- same room: `0`
- same building: `1`
- same movement zone: `2`
- different movement zone: `3`

Special rules:

- if either session is `ONL`, transition cost is `0`.
- if either room is missing/online, transition cost is `0`.
- non-adjacent sessions do not contribute movement cost.

Room movement uses the building suffix after the final `-` in the room code.
The normative building map is:

- `A` or `B` -> `GD4`;
- `T` or `GĐ3` -> `GD3`;
- `G2`, `G3`, `E3`, or `E5` -> `GD2`;
- any room containing `ĐHKHTN` -> the separate `ĐHKHTN` zone;
- an unmapped building name -> a zone with that same name.

The implementation is `scheduler/desktop/src/Scheduler.Domain/RoomMovement.cs`.
Movement cost is evaluated after SAT feasibility and is used to rank only the
bounded set of returned solutions; it is not optimized inside the CNF.

---

## 8) Completeness and Validity

### Existing schedule validity

A schedule is valid under Layer A iff:

- it is complete (all schedulable sessions are assigned), and
- it has zero `HC-*` violations.

### Personal solution validity

A personal selection solution is valid under Layer B iff:

- `actual_choice_count == expected_choice_count`, and
- it has zero `PT-*` violations.

---

## 9) Traceability to Code

- SAT selector and CNF: `scheduler/desktop/src/Scheduler.Application/PersonalSelectionCnf.cs`
- Candidate build and movement ranking: `scheduler/desktop/src/Scheduler.Application/PersonalSelectionService.cs`
- Validator (`HC-*` and `PT-*`): `scheduler/desktop/src/Scheduler.Application/TimetableValidator.cs`
- Room movement cost model: `scheduler/desktop/src/Scheduler.Domain/RoomMovement.cs`

---

## 10) Formal Method Note

For formalization, the primary proof target of current product behavior is Layer B (`PT-*`) plus SAT encoding soundness/completeness relative to those constraints.

Layer A (`HC-*`) remains important for workbook integrity checks and should stay logically consistent with validator behavior.
