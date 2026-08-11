# Tiny UNSAT CNF Fixture

This directory contains a minimal personal-selection CNF used for manual
inspection and future C# formal-artifact regression tests. It is not a release
proof and does not certify the production timetable encoder.

The fixture has two requested teaching-team requests and one candidate for each request:

- variable `1`: `LHP-A1` for `INT0001 + Team A`;
- variable `2`: `LHP-B1` for `INT0002 + Team B`.

Both candidates occupy `Mon-Ca1`, so the exact CNF is:

```text
1 0
2 0
-1 -2 0
```

The unit clauses force both candidates while the conflict clause forbids their
combination. Therefore the formula is UNSAT.

## Files

- `tiny_unsat.cnf`: DIMACS formula;
- `tiny_unsat.varmap.json`: variable-to-selection mapping;
- `tiny_unsat.clauses.json`: clause kind and literal mapping.

The files use the historical study-vector schema and are kept only as static
DIMACS integrity inputs. New production artifacts must be exported by
the C# `FormalArtifactExporter`, which also writes a detached formula hash and the
current verification scripts.
