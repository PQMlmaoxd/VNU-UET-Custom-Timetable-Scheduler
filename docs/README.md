# Documentation

The repository root intentionally contains only the project entry point and
standard GitHub files. Secondary documentation is grouped here or kept beside
the source it describes.

## Product

- [C# desktop architecture and release status](architecture/MIGRATION_CSHARP_CADICAL.md)
- [Desktop developer and release guide](../scheduler/desktop/README.md)
- [Release acceptance checklist](../scheduler/desktop/RELEASE_ACCEPTANCE_CHECKLIST.md)
- [Data handling policy](policies/DATA_HANDLING.md)

## Formal Specification

- [Authoritative hard constraints](../formal/spec/hard_constraints.md)
- [SAT proof-pipeline roadmap](../scheduler/formal/spec/proof_pipeline_learning_roadmap.md)
- [Clause provenance guide](../scheduler/formal/spec/clause_provenance_guide.md)
- [Formal test vectors](../scheduler/formal/tests/test_vectors/)

## Native Solver

- [SolverWorker protocol](../scheduler/desktop/native/SolverWorker/PROTOCOL.md)
- [SolverWorker build guide](../scheduler/desktop/native/SolverWorker/README.md)
- [Optional CakeLPR formal-verification tools](../scheduler/desktop/native/FormalVerification/README.md)
- [Third-party notices](../scheduler/desktop/native/SolverWorker/third_party/NOTICE.md)

## Release

- [Release and GitHub Actions guide](release/RELEASE.md)
- [Public source snapshot policy and integrity](release/SOURCE_SNAPSHOT.md)
