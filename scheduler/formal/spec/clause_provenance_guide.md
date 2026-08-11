# Clause Provenance Guide

## Purpose

`*.clauses.json` explains why each DIMACS clause exists.

This artifact is the bridge between:

- raw SAT syntax, such as `-1 -3 0`, and
- the personal timetable selector semantics, such as “these two teaching units overlap”.

It is an audit aid for human review and formal-method traceability.

---

## Files In A Proof Study Case

Static study fixtures may use their own filename prefix. A production export from
`FormalArtifactExporter` always uses this package layout:

```text
formula.cnf
formula.sha256
variables.json
clauses.json
manifest.json
proof.lrat
proof.lrat.sha256
verification.json
```

The roles are:

- `formula.cnf`: the SAT problem in DIMACS.
- `formula.sha256`: detached hash for the exported formula.
- `variables.json`: SAT variable to teaching-team/teaching-unit/session mapping.
- `clauses.json`: clause to semantic-source mapping.
- `proof.lrat`: solver-produced UNSAT proof artifact.
- `proof.lrat.sha256`: detached proof hash written by the generation step.
- `verification.json`: checker result and observed hashes.

---

## Clause Types

The current selector encoder emits three clause types.

### `ALO`

Means: at least one candidate teaching unit must be chosen for a selected request.

Example:

```text
2 3 0
```

Semantic reading:

- for this request, choose candidate var `2` or candidate var `3`.

### `AMO`

Means: at most one candidate may be chosen for the same selected request.

Example:

```text
-2 -3 0
```

Semantic reading:

- candidates `2` and `3` belong to the same request,
- so they cannot both be selected.

### `CONFLICT`

Means: two candidate teaching units from different selected requests cannot both be selected.

Example:

```text
-1 -3 0
```

Semantic reading:

- candidate `1` and candidate `3` conflict,
- so selecting both would violate personal timetable correctness.

The conflict witness currently has one of these forms:

- `shared_session`: both candidates reuse at least one session id.
- `timeslot_overlap`: one session from the left candidate overlaps one session from the right candidate.

---

## How To Read A Clause Entry

An entry has the shape:

```json
{
    "clause_index": 5,
    "literals": [-1, -3],
    "kind": "Conflict",
    "description": "Two candidate teaching units from different requests cannot both be selected.",
    "witness": {
      "shared_session_ids": [],
      "timeslot_overlaps": [{
        "left_session_id": "s1",
        "left_timeslot": { "session_id": "s1", "day": "Monday", "period": "1", "atomic_periods": [1] },
        "right_session_id": "s3",
        "right_timeslot": { "session_id": "s3", "day": "Monday", "period": "1", "atomic_periods": [1] }
      }]
    }
}
```

Read it as:

- clause 5 is `(-1 v -3)`,
- it exists because variables 1 and 3 represent incompatible teaching units,
- the incompatibility is witnessed by `s1` and `s3` overlapping at the same atomic period.

---

## Why This Matters For Formal Methods

Proof checkers certify CNF-level statements.

They do not know about:

- courses,
- lecturers,
- LHPs,
- sessions,
- timeslots.

Clause provenance helps us connect the proof artifact back to the intended semantics.

It does not by itself prove soundness or completeness, but it makes the encoding contract inspectable.

This is the right intermediate artifact before attempting a more formal soundness/completeness argument.
