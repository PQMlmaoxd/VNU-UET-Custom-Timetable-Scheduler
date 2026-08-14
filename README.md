# VNU-UET-Custom-Timetable-Scheduler

An offline Windows application for selecting a compatible personal timetable
from an existing VNU-UET timetable document.

The product name describes the application brand. It does not imply that the
application creates or publishes a university-wide timetable.

The application does not create or rewrite a university-wide timetable. A user
chooses course and teaching-team requests; the application selects one existing
teaching unit for each request, rejects time conflicts, and returns up to five
distinct options.

## Architecture

- `scheduler/desktop`: C#/.NET 8 WPF/WebView2 application, timetable importers,
  independent validation, packaging, and native solver integration.
- `scheduler/desktop/native/SolverWorker`: isolated C++ process linked to the
  pinned CaDiCaL 3.0.1 source.
- `scheduler/frontend`: React interface bundled into the desktop host.
- `formal/spec`: authoritative constraint definitions.
- `scheduler/formal`: proof-pipeline notes and static formal test vectors.

## Supported Input

The desktop application accepts:

- XLSX files using the current fixed timetable schema.
- The signed, text-native UET PDF template implemented by the PDF importer.

The desktop host is the supported runtime. Unsupported PDF layouts and scanned
PDFs are rejected rather than parsed heuristically.

## Movement Cost

After SAT finds feasible personal timetables, the application assigns each
returned option a movement cost. The cost is not a SAT objective: the solver
enumerates a bounded set of feasible options first, and the application ranks
that returned set from lowest to highest cost.

For selected sessions on the same day, ordered by their fixed atomic periods,
only consecutive sessions contribute. A transition is consecutive when the
previous session ends at atomic period `p` and the next starts at `p + 1`.
Non-consecutive sessions contribute `0`.

The transition cost is:

- `0`: same room;
- `1`: different rooms in the same building;
- `2`: different buildings in the same movement zone;
- `3`: different movement zones.

Online sessions and sessions without a physical room contribute `0`. Movement
zones are derived from the room-building map in
`scheduler/desktop/src/Scheduler.Domain/RoomMovement.cs`. Lower cost is preferred
only among the options returned by the bounded SAT enumeration; the first option
is not claimed to be the global movement optimum.

## Development

The desktop solution targets .NET 8 LTS and uses the SDK pinned in
`scheduler/desktop/global.json`. The current repository pin is SDK `9.0.312`;
this is an SDK choice, not a change to the .NET 8 target framework.

Build the desktop solution from `scheduler/desktop`:

```powershell
dotnet restore Scheduler.sln
dotnet build Scheduler.sln --configuration Release --no-restore
dotnet test Scheduler.sln --configuration Release --no-restore
```

The repeatable Windows release gate is:

```powershell
.\scripts\verify-windows-release.ps1 `
  -SolverWorkerPath .\native\SolverWorker\out\windows-x64\SolverWorker.exe
```

Native worker build instructions are in
[`scheduler/desktop/native/SolverWorker/README.md`](scheduler/desktop/native/SolverWorker/README.md).
Frontend-specific commands are in
[`scheduler/frontend/package.json`](scheduler/frontend/package.json).

## Formal UNSAT Artifacts

For a solver-reported UNSAT result, the desktop application can export a
package containing the exact canonical CNF, variable/clause metadata, hashes,
and scripts for CaDiCaL plus `cake_lpr`.

The export is not itself a proof. `cake_lpr` formally checks the hashed CNF and
the matching LRAT proof. The result is accepted only when the checker exits
successfully and prints `s VERIFIED UNSAT`. This establishes unsatisfiability
of that CNF under the stated toolchain assumptions; it does not independently
prove that the timetable parser or CNF encoder models the product semantics.
The generated CakeLPR checker source used by this optional workflow is included
under `scheduler/desktop/native/FormalVerification/third_party/cake_lpr`.
The pinned CakeML and HOL4 proof/compiler sources are Git submodules. Clone the
repository with `--recurse-submodules` when the complete provenance tree is
needed.

The package may contain course, lecturer, LHP, and session identifiers. Review
it before sharing. See [`docs/policies/DATA_HANDLING.md`](docs/policies/DATA_HANDLING.md).

## Release Status

The source and application are free to use under the MIT License. The repository
can be published without a paid signing certificate or a CakeLPR proof fixture.

Those items are additional release hardening for an official Windows installer
and for claiming a formally verified UNSAT result. The acceptance evidence is
tracked in [`scheduler/desktop/RELEASE_ACCEPTANCE_CHECKLIST.md`](scheduler/desktop/RELEASE_ACCEPTANCE_CHECKLIST.md).

Each Windows release explicitly uploads four assets: the installer, portable ZIP,
standalone tester Diagnostics CLI, and recursive CakeML/HOL4 source bundle. The
installer and ZIP both include `Scheduler.Diagnostics.exe`; opening it with no
arguments performs privacy-safe checks of sibling application files. GitHub exposes
the SHA-256 digest of each asset, so release checksum text files are not uploaded.

## Documentation

The documentation index is [`docs/README.md`](docs/README.md). It links the
architecture record, data-handling policy, release checklist, formal
specification, and native-worker guides without adding secondary pages to the
repository root.

The release procedure is documented in
[`docs/release/RELEASE.md`](docs/release/RELEASE.md). CI runs automatically on
pull requests and pushes to `main`; a version tag creates the Windows Release.

## Licensing

Project source is released under the MIT License. Third-party dependencies keep
their own licenses; notices for bundled or vendored dependencies are maintained
under the desktop release files.
