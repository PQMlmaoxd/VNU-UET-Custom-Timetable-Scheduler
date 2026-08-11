# Proof Pipeline Research Roadmap

## Purpose

This note records a formal-method research path for the current SAT selector and its proof artifacts.

The goal is not only:

- export CNF,
- call a solver,
- get an UNSAT proof,
- run a checker,
- eventually run `cake_lpr`.

The technical objectives are to define:

- what mathematical claim each artifact represents,
- where trust enters and leaves the system,
- what the solver is proving,
- what the checker is checking,
- and how this connects back to the current personal timetable selector.

---

## Current Project Semantics

The active end-user problem is:

- fixed workbook timetable,
- user chooses `(course_code, teaching_team_key)` requests,
- SAT chooses exactly one `LHP` per teaching-team request,
- selected `LHP`s must not overlap,
- feasible solutions are ranked by movement cost.

For formalization, the main proof target is **Layer B** from
`../../../formal/spec/hard_constraints.md`:

- `PT-1 .. PT-7` personal-timetable correctness.

This matters because the proof story should follow the real product semantics, not the old global-rescheduling model.

---

## Trust Stack

The pipeline is a stack of claims:

1. `PT-*` spec says what a correct personal timetable means.
2. The C# encoder translates that problem into CNF.
3. A SAT solver claims the CNF is SAT or UNSAT.
4. A proof artifact explains the UNSAT claim.
5. A proof checker validates the proof artifact against the CNF.
6. A verified checker such as `cake_lpr` reduces trust in the checker implementation itself.

The analysis focuses on the **meaning-preserving links** between these layers.

---

## What Phase 1 Gave Us

Phase 1 is now in place:

- CNF export to DIMACS
- variable metadata export to JSON

Relevant files:

- `scheduler/desktop/src/Scheduler.Application/PersonalSelectionCnf.cs`
- `scheduler/desktop/src/Scheduler.Infrastructure.FormalVerification/FormalArtifactExporter.cs`

Outputs:

- `formula.cnf` and `formula.sha256`
- `variables.json` and `clauses.json`
- `manifest.json` and the PowerShell proof commands

This is the point where the SAT problem becomes visible and inspectable.

---

## The Right Learning Sequence

The sequence below is organized by increasing **proof object sophistication**, not
by tool replacement alone. Each phase has a distinct artifact and acceptance
criterion.

### 1. CNF and Resolution Proofs

Learn first:

- what a clause means,
- what it means for a CNF to be satisfiable,
- how resolution derives a new clause,
- how deriving the empty clause proves UNSAT.

What to do in this project:

- export a tiny CNF,
- open the `.cnf` and `.varmap.json`,
- manually read each variable as a teaching-team request choosing an LHP,
- manually explain each clause as one of:
  - at-least-one,
  - at-most-one,
  - cross-request conflict.

Learning outcome:

- you stop seeing CNF as anonymous integers,
- and start seeing it as a logical encoding of your timetable semantics.

### 2. How DPLL/CDCL Produces a Proof

Learn next:

- decision literals,
- unit propagation,
- conflict analysis,
- clause learning,
- backjumping,
- why a CDCL solver can emit an UNSAT proof.

What to do in this project:

- take a very small UNSAT selector instance,
- predict by hand why it is UNSAT,
- then compare that human explanation with the learned-clause style explanation produced by the solver/proof format.

Learning outcome:

- you understand that the solver is not “magically knowing UNSAT”,
- it is building a derivation trail that can be checked afterward.

### 3. DRAT, LRAT, and LPR

Learn the role of each format:

- `DRAT`: compact and solver-friendly proof logging format.
- `LRAT`: adds enough witness information to make checking simpler and more direct.
- `LPR`: proof-oriented representation used by the CakeML side of the story.

What to focus on conceptually:

- DRAT is easier for solvers to emit.
- LRAT/LPR are easier or more structured for checkers.
- proof formats are not interchangeable text dumps; they encode different checking obligations.

Learning outcome:

- you understand why “solver emits proof” and “verified checker accepts proof” are usually separated by one or more translation/checking steps.

### 4. Solver vs Checker

Learn the trust split:

- the solver searches,
- the checker verifies.

The important formal-method idea is that:

- you do **not** have to trust the solver implementation for UNSAT,
- if you trust a small checker and the proof format semantics.

Learning outcome:

- you understand why proof-producing SAT is interesting for formal methods at all.

### 5. CakeML and `cake_lpr`

Learn what is being verified:

- not the whole SAT solver,
- but the proof checker for a proof format.

This means the verified claim is roughly:

- if `cake_lpr` accepts the proof for CNF `F`, then `F` is UNSAT.

Learning outcome:

- you understand exactly where the theorem-proving effort pays off in the trust chain.

### 6. Reconnect Everything to This Project

The final understanding should be:

- the CNF came from the C# encoding of the personal-selector problem,
- the proof certifies UNSAT of that CNF,
- the remaining trusted question is whether your encoding faithfully captures `PT-*` semantics.

That is where the real formal-method question lives:

- not only “is the CNF UNSAT?”
- but also “does UNSAT of this CNF mean no valid personal timetable exists?”

---

## Phase 2: UNSAT Proof Logging

## Goal

Generate an UNSAT proof artifact for the exported selector CNF.

## Engineering Task

Pick one solver/build that supports proof logging and freeze it for research.

Candidates:

- CaDiCaL or another CDCL solver with proof output support
- a format such as `DRAT`, `LRAT`, or `LPR`

Important point:

- do not start by chasing “the best solver”.
- start by choosing a solver/proof format combination that gives a stable, inspectable artifact.

## Recommended Deliverables

For one UNSAT case, save:

- `case.cnf`
- `case.varmap.json`
- `case.proof.<format>`
- `case.solve.log`
- `case.notes.md`

The `notes` file should explain:

- why you expected UNSAT,
- what the solver said,
- what proof format was produced,
- what you do and do not trust yet.

## Learning Focus

At this phase, do not optimize desktop bridge behavior.

Instead, study:

- how the proof file grows,
- whether learned information corresponds to your intuitive conflict structure,
- how an UNSAT result on the CNF relates back to the selected timetable pairs.

---

## Phase 3: External Proof Checker

## Goal

Validate the solver’s UNSAT claim with a checker that is separate from the solver.

## Candidate Tools

- `drat-trim`
- an `LRAT` or `LPR` checker

## Why This Phase Matters

This is the first point where the workflow becomes formally interesting in a serious way.

Without a checker, the solver says:

- “trust me, UNSAT”.

With a checker, the workflow says:

- “here is a proof object, and here is a separate verifier for that object”.

## Recommended Deliverables

For the same UNSAT case, save:

- `case.check.log`
- translated proof artifacts if needed, such as:
  - `case.proof.drat`
  - `case.proof.lrat`
  - `case.proof.lpr`

The case notes should now answer:

- what format did the solver emit?
- what conversion steps were required?
- what exactly did the checker validate?
- what is still trusted after this step?

## Learning Focus

A reviewer should be able to explain:

- why checker correctness matters more than solver correctness for UNSAT certification,
- why some formats are easier to generate while others are easier to check,
- and what information is lost or gained between DRAT, LRAT, and LPR.

---

## Phase 4: `cake_lpr`

## Goal

Replay the proof artifact through a verified checker.

## Mindset

Do not treat `cake_lpr` as the next command in a shell pipeline.

Treat it as the point where the trust story changes from:

- “I trust this external checker implementation”

to:

- “I trust the theorem-proved semantics and generated executable of the checker”.

## Recommended Deliverables

For one case, save:

- the exact proof artifact consumed by `cake_lpr`
- the exact checker invocation
- the checker output
- a short note describing what theorem-level guarantee this gives you

## Learning Focus

At this phase, the technical note must explain:

- what property `cake_lpr` verifies,
- what it does not verify,
- and why a verified checker still does not by itself prove the application encoder correct.

---

## The Main Formal-Method Question in This Project

The deepest question is not:

- “can a SAT solver solve my selector?”

It is:

- “does my CNF faithfully encode the `PT-*` semantics of the personal timetable problem?”

This splits into two directions:

### Soundness of the Encoding

If the CNF is satisfiable, then the decoded model should correspond to a valid personal timetable satisfying `PT-*`.

### Completeness of the Encoding

If a valid personal timetable exists under `PT-*`, then there should exist a satisfying assignment of the CNF.

Studying solver tooling alone does not address these questions.
They are the central soundness and completeness questions for the encoding.

---

## Recommended Research Workflow

Use a small-case-first workflow.

### Step A: Start with Tiny Synthetic UNSAT Cases

Do not begin with the full workbook.

Start with cases like:

- 2 selected teaching-team requests,
- 1 candidate per pair,
- forced overlap,
- obvious UNSAT.

Reason:

- the CNF can be understood manually,
- manually predict UNSAT,
- and manually relate the proof back to semantics.

This workflow minimizes ambiguity and keeps each proof step reviewable.

### Step B: Export Artifacts

For each case, collect:

```text
case.cnf
case.varmap.json
case.proof.*
case.solve.log
case.check.log
case.notes.md
```

### Step C: Write Semantic Notes

For each case, explain:

1. what the original teaching-team requests were,
2. why they were SAT or UNSAT semantically,
3. which clauses in the CNF capture that reason,
4. what the proof artifact is certifying,
5. what remains outside the proof certificate.

### Step D: Only Then Scale Up

After you understand tiny cases, move to:

- real workbook UNSAT cases,
- more candidate LHPs,
- larger proof artifacts,
- conversion to `LRAT` or `LPR`,
- verified checking with `cake_lpr`.

---

## Concrete Command-Line Research Pipeline

The production desktop workflow uses the pinned CaDiCaL CLI and `cake_lpr`.

The stable shape of the workflow should be:

### 1. Export CNF from the desktop application

Use `Trợ giúp > Xuất gói kiểm chứng UNSAT` in the desktop application and
choose the output ZIP location.

The desktop export uses the canonical C# encoder and writes the formula hash and
metadata required by the verification scripts.

### 2. Generate the LRAT proof with CaDiCaL

```powershell
.\generate-unsat-proof.ps1 `
  -CadicalPath 'C:\tools\cadical.exe' `
  -ExpectedCadicalSha256 '<approved SHA-256>'
```

### 3. Verify the proof with `cake_lpr`

```powershell
.\verify-unsat-proof.ps1 `
  -CakeLprPath 'C:\tools\cake_lpr.exe' `
  -ExpectedCakeLprSha256 '<approved SHA-256>'
```

The verifier checks externally supplied formula and proof hashes, checker exit code and exact
`s VERIFIED UNSAT` marker. The approved executable hashes are release policy,
not properties established by the CNF itself.

---

## What to Learn at Each Artifact Boundary

### CNF boundary

Question:

- how did semantic constraints become clauses?

### Solver boundary

Question:

- how did search and conflict analysis derive UNSAT?

### Proof-format boundary

Question:

- what information is needed so a separate checker can replay trustably?

### External-checker boundary

Question:

- what trust has now moved away from the solver?

### Verified-checker boundary

Question:

- what trust has now moved away from the checker implementation?

### Encoding-correctness boundary

Question:

- even if UNSAT is certified, does the CNF still match the intended personal-timetable semantics?

---

## Suggested First Milestone

The first research milestone is a small end-to-end proof package.

It is:

1. choose one tiny UNSAT selector case,
2. export its CNF and varmap,
3. explain every variable and every clause,
4. obtain an UNSAT proof artifact,
5. check it with an external checker,
6. write a one-page note describing the trust story.

The milestone is complete when the artifact, semantic mapping, proof output, and
checker assumptions are recorded in a reviewable technical note.

---

## Suggested Next Engineering Step

The checked-in tiny and branch CNF fixtures provide the controlled cases for
reviewing clause meaning before using large timetable instances. Production
proof packages are generated by the desktop exporter and reviewed with approved
CaDiCaL and `cake_lpr` binaries.
