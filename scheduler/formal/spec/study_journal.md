# Study Journal

## Why this file exists

This file records research observations, unresolved questions, and links to
normative specifications. It is non-normative: implementation and proof claims
must be established by the formal specifications, source code, and test evidence.

---

## Current understanding

### 1. The current product problem

The active problem is not global rescheduling.

It is:

- fixed workbook timetable,
- choose `(course_code, teaching_team_key)` requests,
- select exactly one `LHP` per teaching-team request,
- forbid overlaps between chosen `LHP`s,
- rank feasible solutions by movement cost.

### 2. What the CNF means

The exported CNF is the SAT encoding of the selector problem.

Each SAT variable means:

- choose one concrete LHP candidate for one selected teaching-team request.

Each clause currently comes from one of:

- at-least-one for a selected teaching-team request,
- at-most-one for a selected teaching-team request,
- cross-request conflict caused by overlap or session reuse.

### 3. Tiny UNSAT case

In the tiny study case:

- pair 0 has only one candidate,
- pair 1 has only one candidate,
- both sessions occur at the same timeslot,
- so both choices are forced,
- but they cannot both be true,
- therefore the CNF is UNSAT.

### 4. Human proof vs proof artifact

Human-readable proof:

- `(1)`
- `(2)`
- `(-1 v -2)`
- derive `(-2)`
- derive empty clause `()`

Machine proof artifact:

- may be much shorter,
- may directly add the empty clause,
- may include deletion commands that are administrative rather than logical.

### 5. Meaning of `0` in the proof

In the DRUP-style proof file, a line containing only `0` means:

- add the empty clause.

Once the empty clause is justified, UNSAT is established.

This is close in spirit to contradiction-based reasoning in propositional logic:

- the formula leads to contradiction,
- so the original clause set is inconsistent.

### 6. Meaning of delete lines

A line like:

```text
d -1 -2 0
```

means:

- delete clause `(-1 v -2)` from the active clause database.

This does not introduce a new theorem.
It is a proof-management step.

### 7. What proof checking currently establishes

The current learning checker establishes:

- this exported CNF is UNSAT.

It does not yet establish:

- that the C# encoding is fully correct with respect to the intended `PT-*` semantics.

---

## Formal-method interpretation

The important distinction is:

- solver result correctness,
- versus encoding correctness.

Proof logging and proof checking reduce trust in the SAT solver for UNSAT claims.

They do **not** by themselves prove:

- soundness of the encoding,
- completeness of the encoding.

For this project, the main formal-method question is:

- does `UNSAT(CNF)` really mean `no valid personal timetable exists`?

That requires completeness of the encoding.

---

## Current trust story

We have moved from:

- "the solver said UNSAT"

to:

- there is a CNF,
- there is a proof artifact,
- there is a separate replay/check step,
- therefore trust in the UNSAT claim is stronger.

We have **not** yet reached:

- verified proof checking,
- proved encoding soundness/completeness.

---

## Good next questions

1. In what exact sense is empty-clause proof related to contradiction or Robinson-style reasoning?
2. What is the precise difference between resolution proof, RUP/DRUP proof, and DRAT proof?
3. Why can machine proofs be much shorter or much less human-readable than hand proofs?
4. What would a slightly larger UNSAT proof look like?
5. How should soundness and completeness be stated for the personal selector CNF?
6. What part of the trust chain is improved by `drat-trim`?
7. What additional part is improved by `cake_lpr`?

---

## Repository practice

Do not keep research understanding only in chat.

Keep three things in the repository:

1. artifacts
2. short explanatory notes
3. open questions

This keeps the research record cumulative and reviewable.
