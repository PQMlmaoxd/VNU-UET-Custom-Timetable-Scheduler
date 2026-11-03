# Documentation

Start with the [project README](../README.md) for downloads, supported inputs, the scheduling workflow, and the limitations of SAT and formal verification. The guides below cover development, release, and evidence in more detail.

## Using and building the desktop app

- [Desktop development and verification](../scheduler/desktop/README.md)
- [C# architecture and solver integration](architecture/MIGRATION_CSHARP_CADICAL.md)
- [Windows release procedure](release/RELEASE.md)
- [Release acceptance checklist](../scheduler/desktop/RELEASE_ACCEPTANCE_CHECKLIST.md)
- [Data handling policy](policies/DATA_HANDLING.md)

## Constraints and formal verification

- [Authoritative hard constraints](../formal/spec/hard_constraints.md)
- [SAT proof-pipeline roadmap](../scheduler/formal/spec/proof_pipeline_learning_roadmap.md)
- [Clause provenance guide](../scheduler/formal/spec/clause_provenance_guide.md)
- [Formal test vectors](../scheduler/formal/tests/test_vectors/)
- [Optional CakeLPR tools](../scheduler/desktop/native/FormalVerification/README.md)

## Native solver and project maintenance

- [SolverWorker protocol](../scheduler/desktop/native/SolverWorker/PROTOCOL.md)
- [SolverWorker build guide](../scheduler/desktop/native/SolverWorker/README.md)
- [Public source snapshot policy](release/SOURCE_SNAPSHOT.md)
- [Third-party notices](../scheduler/desktop/native/SolverWorker/third_party/NOTICE.md)
- [Contributing](../CONTRIBUTING.md) and [security reporting](../SECURITY.md)
