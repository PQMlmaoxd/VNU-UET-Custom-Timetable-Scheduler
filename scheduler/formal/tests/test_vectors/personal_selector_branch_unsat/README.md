# Branch UNSAT CNF Fixture

This static fixture demonstrates an UNSAT selector with a non-empty derived
clause. It is a study input, not a production proof artifact.

The clauses are:

```text
1 0
2 3 0
-2 -3 0
-1 -2 0
-1 -3 0
```

Variable `1` is forced. Pair 1 must select variable `2` or `3`, but both are in
conflict with variable `1`; the formula is therefore UNSAT.

## Files

- `branch_unsat.cnf`: DIMACS formula;
- `branch_unsat.varmap.json`: variable-to-selection mapping;
- `branch_unsat.clauses.json`: clause kind and literal mapping.

These files use the historical study-vector schema and are kept only as static
DIMACS integrity inputs. New production artifacts must be exported by the C#
formal-artifact exporter.
