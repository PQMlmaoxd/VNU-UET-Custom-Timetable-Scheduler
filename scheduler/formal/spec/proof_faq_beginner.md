# Proof Format FAQ

## 1. How is a valid DRUP step different from a resolution step?

Think of them like this:

- a **resolution step** tells you exactly which two clauses were combined,
- a **DRUP step** only gives you the new clause and asks the checker to verify that it is safe to add.

### Resolution style

Example:

- `(1)`
- `(-1 v -2)`
- therefore derive `(-2)`

This representation is useful because the local reason is visible immediately.

### DRUP style

Instead of naming the parent clauses, the proof just says:

- add `(-2)`

Then the checker asks:

- if I temporarily assume the opposite, namely `(2)`, does the current clause set become UNSAT?

If yes, then `(-2)` is accepted.

So the difference is:

- resolution shows the derivation explicitly,
- DRUP lets the checker rediscover enough of the contradiction on its own.

That is why DRUP is often shorter and less readable for humans.

---

## 2. How should soundness and completeness be stated for the current personal selector?

Let:

- `Problem` be the user selection problem,
- `Encode(Problem)` be the exported CNF,
- `Decode(Model)` map a SAT model back to selected LHP choices,
- `Valid(Choices, Problem)` mean the choices satisfy the intended `PT-*` semantics.

Then the two key theorems are:

### Soundness

If a SAT model satisfies the CNF, then decoding it gives a valid personal timetable choice.

Informally:

- SAT model  ->  valid timetable choice

Formally:

```text
For every model M,
if M satisfies Encode(Problem),
then Valid(Decode(M), Problem).
```

### Completeness

If a valid personal timetable choice exists, then there is a SAT model for the CNF.

Informally:

- valid timetable choice  ->  SAT model

Formally:

```text
For every valid choice C,
if Valid(C, Problem),
then there exists a model M such that
M satisfies Encode(Problem)
and Decode(M) = C.
```

Why completeness matters especially for UNSAT:

- if the CNF is UNSAT,
- and the encoding is complete,
- then no valid timetable choice exists.

Without completeness, UNSAT only means:

- the CNF has no model,

not necessarily:

- the original scheduling problem has no solution.

---

## 3. If the proof checker is verified, what trusted base remains?

Even with a verified checker, you still trust some things.

For this project, the main remaining trusted pieces are:

1. the CNF export itself,
2. the claim that the CNF correctly encodes the intended `PT-*` semantics,
3. the exact proof artifact given to the checker,
4. the runtime environment and file I/O around the checker.

The big improvement is this:

- you no longer need to trust the SAT solver very much for the UNSAT claim,
- because the verified checker independently validates the proof.

So verified checking removes a large chunk of trust from the solver side,
but not from the encoding side.

---

## 4. Why can the empty clause be added directly without listing all intermediate clauses?

Because proof formats like DRUP are designed for machine checking, not for human storytelling.

If the checker can confirm that adding the empty clause is justified, then the proof is valid.

The solver does not have to print every human-intuitive intermediate step.

In the tiny study case, adding `0` means:

- the current active clause database is already contradictory,
- so the empty clause is justified immediately.

This may feel abrupt to a beginner because a human would usually show:

- one intermediate clause,
- then another,
- then contradiction.

But machine proofs are allowed to skip that human presentation layer.

They only need to be checkable.

---

## 5. What are DRUP, DRAT, LRAT, and LPR from an operational point of view?

Do not first think of them as file formats.
Think of them as different compromises between:

- solver convenience,
- checker convenience,
- and trust clarity.

### DRUP

Operational summary:

- "Here is a clause I want to add."
- "Checker, please confirm that its negation causes contradiction."

Useful for illustrating proof replay.

### DRAT

Operational summary:

- more powerful and more flexible than DRUP,
- easier for high-performance solvers to emit,
- but less transparent for a beginner.

DRAT is a more solver-friendly proof language.

### LRAT

Operational summary:

- the proof gives the checker more guidance,
- so checking becomes more direct and structured,
- often better when you care about a small trusted checker.

LRAT adds explicit hints that reduce checker guesswork.

### LPR

Operational summary:

- a proof representation aligned with the verified-checking side,
- especially relevant when moving toward CakeML tooling.

LPR is part of the bridge from raw SAT proof artifacts to formally verified proof replay.

### Simple mental summary

- `DRUP/DRAT`: easier for solvers to emit
- `LRAT/LPR`: easier or cleaner for trustworthy checking

That is not the full technical story, but it is a useful operational distinction.

---

## When can the project move to CakeML / `cake_lpr`?

The CakeML handoff requires these technical conditions:

1. a reviewer can read a small CNF and explain every clause semantically,
2. a reviewer can read a small proof artifact and explain at least one non-empty derived clause,
3. you understand what the external checker is certifying,
4. you understand what is still outside the certificate,
5. we have chosen a concrete proof-producing solver and a proof format path that can be translated to what `cake_lpr` expects.

The current repository has a working UNSAT artifact export path, but the complete
handoff still depends on the release evidence listed below.

Remaining release evidence for a CakeML-backed verification claim:

- an approved `cake_lpr` binary hash and a successful verification of an authorized fixture,
- an explicit `LRAT/LPR` proof-format path matching the selected checker,
- a clearer statement of encoding soundness/completeness goals.

The implementation exports the required CNF package and verification scripts.
Formal verification remains a release claim only when the approved checker,
matching proof, hashes, and fixture evidence are recorded.
