# C# Desktop Architecture and Release Status

**Status:** Active product architecture
**Target:** Windows desktop, .NET 8, WPF, WebView2, and an isolated CaDiCaL worker

## Product Scope

The application reads a fixed timetable document and selects compatible existing
class groups for the user's `(course_code, teaching_team_key)` choices. It does not
reschedule the institution's timetable and never writes back to the source
document.

Supported desktop inputs are XLSX and the signed text-native PDF template. The
React interface is bundled into the WPF host and communicates through the local
WebView bridge. No server, database, HTTP listener, or Python runtime is needed
by the product.

## Architecture

| Layer | Location | Responsibility |
| --- | --- | --- |
| Domain | `src/Scheduler.Domain` | Immutable scheduling models and movement semantics |
| Application | `src/Scheduler.Application` | Candidate selection, CNF encoding, validation, ranking |
| XLSX import | `src/Scheduler.Infrastructure.Xlsx` | Fixed workbook schema import |
| PDF import | `src/Scheduler.Infrastructure.Pdf` | Coordinate-aware signed-template import |
| Native solver | `native/SolverWorker` | Isolated CaDiCaL 3.0.1 process and protocol |
| Formal artifacts | `src/Scheduler.Infrastructure.FormalVerification` | Deterministic UNSAT CNF export |
| Desktop host | `src/Scheduler.Desktop` | WPF shell, WebView2 bridge, logging, packaging |
| Frontend | `../frontend` | React UI and bridge client |

The C# validator is independent of the solver adapter. Every native model is
checked against the CNF and personal-selection constraints before it reaches the
UI. The solver enumerates a bounded set of feasible models; the application
ranks the returned set by movement cost.

## Bridge Contract

Bridge protocol version 1 provides:

- `validate_workbook`
- `solve_workbook`
- `cancel_command`
- `set_theme`
- `desktop_ready`
- `export_unsat_artifact`

The bridge is the only runtime transport. Do not reintroduce server paths,
browser HTTP endpoints, or transport-specific business logic into the frontend.

## Build and Test

The repository pins SDK `9.0.312` in `global.json`; projects target .NET 8.
The Windows native worker must be built with a current MSYS2 MinGW-w64 toolchain.
The old standalone MinGW installation is unsupported.

Frontend checks:

```powershell
cd scheduler/frontend
npm.cmd ci
npm.cmd test
npm.cmd run build
npm.cmd run test:e2e
```

Desktop release gate:

```powershell
cd scheduler/desktop
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File scripts/verify-windows-release.ps1 `
  -SolverWorkerPath native/SolverWorker/out/windows-x64/SolverWorker.exe `
  -ReleaseVersion <release-version>
```

The gate covers native worker checks, .NET build/tests, frontend tests/build,
publish output, and SBOM generation. It does not replace manual WebView2,
clean-machine, signing, or interactive acceptance checks.

## Formal UNSAT Artifacts

`Xuất gói kiểm chứng UNSAT` exports the canonical C# CNF, variable/clause
metadata, manifest hashes, provenance, and PowerShell commands. Export is not a
proof.

The external CaDiCaL CLI generates an LRAT proof. `cake_lpr` verifies the exact
`formula.cnf` identified by the externally preserved export hash. A result is formally verified
only when the checker exits successfully and emits `s VERIFIED UNSAT`. This
certifies that CNF, not the timetable parser, C# encoder, or timetable semantics,
has an accepted UNSAT certificate.

Proof packages contain course, lecturer, class-group, and session identifiers.
Treat them as sensitive local artifacts; do not attach them to tickets without
review.

## Release Evidence

Implementation status and release evidence are separate. A release is not
approved until the following are recorded for the exact source revision:

- native worker and CaDiCaL provenance/hash;
- .NET, frontend, native integration, and installer results;
- clean-machine and interactive WebView2 acceptance for an official installer;
- dependency/SBOM review and an optional signing decision;
- an approved UNSAT fixture and matching `cake_lpr` proof only when formal
  verification is part of the release claim.

Use [`../../scheduler/desktop/RELEASE_ACCEPTANCE_CHECKLIST.md`](../../scheduler/desktop/RELEASE_ACCEPTANCE_CHECKLIST.md)
as the manual sign-off record.
