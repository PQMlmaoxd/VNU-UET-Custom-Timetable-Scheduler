# Trust Boundary for the Tiny UNSAT Fixture

This fixture demonstrates the distinction between a semantic claim and a CNF
claim. Its clauses visibly encode two forced choices and one overlap conflict,
so the CNF is easy to inspect as an UNSAT example.

The current C# product workflow adds separate boundaries:

1. the C# encoder creates the CNF;
2. CaDiCaL generates an LRAT proof;
3. `cake_lpr` checks the exact hashed CNF and proof.

The checker certifies the CNF supplied to it. It does not independently prove
that the timetable parser, encoder, or scheduling semantics are correct. Those
claims require the C# validator, compatibility tests, formal specification and
approved release evidence.
