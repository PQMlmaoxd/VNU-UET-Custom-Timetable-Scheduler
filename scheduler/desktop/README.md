# VNU-UET-Custom-Timetable-Scheduler

This directory contains the supported offline Windows desktop implementation.
The public product runtime is C#/.NET with an isolated native CaDiCaL worker.

## Current scope

- `Scheduler.Domain` ports pure workbook and timetable value semantics.
- `Scheduler.Infrastructure.Xlsx` preserves the legacy `Sheet3` schema and discovers compatible timetable sheets from normalized headers. It supports reordered columns and common `BT`/`LT+BT` session labels without relying on a workbook-specific sheet name.
- `Scheduler.Infrastructure.Pdf` parses the signed text-native UET timetable PDF with
  coordinate-aware PdfPig extraction and maps it to the same scheduling contract. It is
  desktop-only for now; scanned PDFs/OCR and unknown templates are rejected.
- `Scheduler.Application` contains the independent validator, fixed-workbook candidate
  builder, deterministic personal-selection CNF encoder, and post-SAT movement-cost
  ranking. A teaching team is one cohort-connected LHP unit, so LT/BT/TH sessions
  in the same unit all contribute to conflict occupancy and cost calculation.

### Movement cost

Movement cost is calculated after feasibility, not optimized in the SAT formula.
For sessions on the same day, only adjacent atomic periods contribute. The
transition score is `0` for the same room, `1` for different rooms in one
building, `2` for different buildings in one movement zone, and `3` for different
zones. Online or roomless transitions score `0`. The UI ranks the bounded set of
solutions returned by SAT; it does not claim global movement optimality.
- `native/SolverWorker` statically links pinned CaDiCaL 3.0.1 and exposes a
  one-request-per-process NDJSON SAT protocol documented in
  `native/SolverWorker/PROTOCOL.md`.
- `Scheduler.Infrastructure.NativeSolver` starts an isolated worker process and
  rejects malformed, duplicate, incomplete, or CNF-unsatisfying models.
- `PersonalSelectionService` materializes every accepted model and independently
  applies PT-1 through PT-7 before exposing a solution.
- `Scheduler.Desktop` provides a Windows WPF/WebView2 host and a versioned, local
  request/response bridge. The React bundle uses this bridge as its product transport.
- `Scheduler.Diagnostics` is a separate, privacy-safe managed console executable. It
  checks the isolated worker, packaged application structure/manifest, and XLSX/PDF input
  without launching the UI. Its commands, privacy defaults, and Windows single-file
  publish script are documented in `src/Scheduler.Diagnostics/README.md`. The tagged
  release bundles it with the installer and portable ZIP, adds a Start Menu-only
  Diagnostics shortcut, and also publishes it as a separate diagnostics asset
  `VNU-UET-Custom-Timetable-Scheduler-<version>-Diagnostics-win-x64.exe`.
- The host and React UI share `system`, `light`, and `dark` theme preferences. The
  choice is stored locally, applied before the document loads, and synchronizes the
  WPF menu, startup shell, and supported Windows title bar.
- Product branding is tracked under `../branding`. The landscape logo is used by
  the React and native startup surfaces, while the padded multi-resolution
  `app.ico` is embedded in the Windows executables and used by the installer.
- Startup shows a native loading shell immediately. It remains visible until React
  confirms `desktop_ready`; navigation failures, WebView process failures, and a
  12-second readiness timeout show a retry action instead of a blank window.
- `validate_workbook` is implemented end-to-end: it accepts a bounded in-memory XLSX or
  supported PDF upload, writes a per-command temporary file, imports it off the UI thread,
  validates the fixed schedule, then returns the current React `ValidateExistingResponse`
  shape without a local server.
- `solve_workbook` is implemented through `PersonalSelectionService`; it materializes
  and independently validates every model before mapping the current `RescheduleResponse`
  shape. Its default adapter requires a packaged `SolverWorker.exe` beside the desktop
  executable, or, in Debug builds, `SCHEDULER_SOLVER_WORKER` pointing to one. Release
  builds accept an external worker only when `SCHEDULER_ALLOW_EXTERNAL_SOLVER=1`. A
  missing worker is a visible command error, never a network fallback.
- XLSX import ignores hidden timetable sheets. A physical row marked `Thông báo sau`
  quarantines its complete course/LHP offering instead of creating a partial candidate;
  complete offerings remain selectable. A partial import can solve the retained
  offerings, but it cannot produce a formal UNSAT certificate.
- The React "Dừng solver" action cancels the active bridge command by request ID. The
  WPF host propagates that cancellation through application orchestration to
  `NativeSolverClient`, which terminates a worker process that does not stop promptly.
  Cancellation is propagated through the local bridge to the native solver process.
- The host writes bounded, rotating JSONL diagnostics under
  `%LOCALAPPDATA%\SchedulerDesktop\logs`. Each entry contains only timestamp,
  correlation ID, normalized command, outcome, elapsed time, application/protocol
  version, and solver version. It never records workbook payloads, names, paths, raw
  selections, or exception details.
- The native `Trợ giúp` > `Thông tin kỹ thuật` menu shows the application version,
  pinned CaDiCaL version and commit, bridge protocol, detected WebView2 runtime,
  and the local activity-log directory. It deliberately excludes workbook and
  selection data.
- `Trợ giúp` > `Xuất gói hỗ trợ` creates a user-selected ZIP with release
  diagnostics and re-sanitized command metadata only. It intentionally excludes
  workbook content/names, paths, selections, correlation IDs, payloads, and
  exception details. Review the ZIP before sharing it.
- When a solve reports UNSAT, the result screen offers `Xuất gói kiểm chứng UNSAT`.
  The desktop exports the exact CNF produced by the application encoder together
  with variable/clause metadata, source hashes, provenance, and PowerShell commands.
  The package does not claim a formal proof by itself. `cake_lpr` checks the exact
  `formula.cnf` identified by an externally preserved export hash; it does not certify the timetable
  parser, the C# encoder, or the scheduling semantics. Run `verify-unsat.ps1` with
  approved CaDiCaL and `cake_lpr` binaries. Formal status requires exit code `0`,
  the exact `s VERIFIED UNSAT` marker, externally supplied matching formula/proof hashes, and recorded
   tool provenance. The complete CakeLPR source is included under
   `native/FormalVerification/third_party/cake_lpr`; generated Windows binaries
   remain release artifacts and are not committed to source.

## Verification

Run these .NET commands from this directory on Windows with the SDK pinned by
`global.json` (`9.0.312` in the current repository):

```powershell
dotnet restore Scheduler.sln
dotnet build Scheduler.sln --configuration Release --no-restore
dotnet test Scheduler.sln --configuration Release --no-restore
dotnet run --project src/Scheduler.Desktop -- --web-root ..\frontend\dist
```

To publish and smoke-test only the standalone diagnostics CLI, without changing the
desktop release package, run:

```powershell
.\scripts\publish-windows-diagnostics.ps1
```

The release gate runs this command, bundles the resulting exact file in the portable
and installer package before metadata is finalized, and copies it to the
product-qualified diagnostics asset name above. Launching Diagnostics without
arguments tests sibling app/worker files without opening the GUI and waits only for
an Explorer-owned console; explicit CLI commands always return immediately.

The last command is the Debug development host. `--web-root` and
`SCHEDULER_WEB_ROOT` are unavailable in Release builds; release packaging must copy the
built React files into the application's `web/` directory, which is the only trusted
web root for the packaged application.

After building `SolverWorker.exe` with the MSYS2 command in
`native/SolverWorker/README.md`, publish a Windows runtime directory with:

```powershell
.\scripts\publish-windows.ps1 -SolverWorkerPath .\native\SolverWorker\out\windows-x64\SolverWorker.exe
```

The generated release is written under ignored `artifacts/windows-x64/` and verifies the
copied native worker version, solver self-test, and protocol self-test before the command
succeeds.

### Debug diagnostic package

The manual **Windows Debug Solver Diagnostics** workflow creates a separate,
self-contained `win-x64` Debug artifact. It does not create a GitHub Release or change
the production `v0.1.0` package. The artifact contains the WPF host, React bundle,
native worker, and direct SAT/UNSAT diagnostics.

To build it from the GitHub Actions page, open **Actions > Windows Debug Solver
Diagnostics > Run workflow**, then download the `scheduler-desktop-debug-*` artifact.
Keep the extracted files together. On the affected machine, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\run-solver-diagnostics.ps1 -RootPath $PWD -FailOnCheckFailure
```

Review `solver-diagnostics.json` before sharing it because it can contain the local
username, application paths, and environment values. If the direct SAT and UNSAT checks
pass, start `Scheduler.Desktop.exe` from the same directory and test the UI. The package
is unsigned and targets 64-bit Windows; endpoint security may still block it.

To run the repeatable automated release gate before publishing a preview, use:

```powershell
.\scripts\verify-windows-release.ps1 -SolverWorkerPath .\native\SolverWorker\out\windows-x64\SolverWorker.exe
```

It runs the worker checks, restore/build/test with the real worker integration
enabled, React dependency install/tests/build, and the publish-directory check.
Use `-SkipExternalFixtureTests` for a fixture-free CI run; use
`-RequireExternalFixtures` with the approved workbook/PDF environment variables
when the compatibility tests must run.
It does not replace a manual WebView2 interaction check or a clean-machine
installation test.

Each release verification also creates `sbom.cdx.json` in the publish directory.
It is a deterministic CycloneDX 1.5 inventory of resolved runtime .NET packages,
the frontend npm lockfile (development packages are labelled), and pinned
CaDiCaL provenance. Review it before publishing; generation is not a substitute
for a license or vulnerability review.

### Installer

The installer is an Inno Setup per-user installation. It writes under
`%LOCALAPPDATA%\Programs\VNU-UET Custom Timetable Scheduler`, never needs administrator rights,
includes the published application, tester diagnostics CLI, native worker, release
manifest, and CaDiCaL license notices, SBOM, and creates no post-install auto-run
action.

Build and smoke-test an installer on a machine that already has the WebView2
Runtime and Inno Setup 6 installed:

```powershell
.\scripts\build-installer.ps1 `
  -SolverWorkerPath .\native\SolverWorker\out\windows-x64\SolverWorker.exe `
  -Version 1.0.0 `
  -SmokeTest
```

This checks silent install/uninstall and the installed desktop executable, worker,
web bundle, license notice, and release manifest. It does not launch the WPF UI.
Without a runtime input, the installer stops with an actionable message on a
machine that lacks WebView2.

For an offline-capable release, supply an official Microsoft **Evergreen
Standalone** WebView2 Runtime installer together with its published HTTPS source
URL and SHA-256. The script verifies the hash before embedding the input and
records its provenance in `release-manifest.json`:

```powershell
.\scripts\build-installer.ps1 `
  -SolverWorkerPath .\native\SolverWorker\out\windows-x64\SolverWorker.exe `
  -Version 1.0.0 `
  -WebView2InstallerPath C:\release-inputs\MicrosoftEdgeWebView2Setup.exe `
  -WebView2Sha256 <pinned-sha256> `
  -WebView2SourceUrl https://developer.microsoft.com/microsoft-edge/webview2/ `
  -SmokeTest
```

Do not substitute an unverified file from a local package cache. A bootstrapper
may require network access and does not qualify as an offline release input.
The generated setup executable is under ignored `artifacts/installer/`.

### Release acceptance

`RELEASE_ACCEPTANCE_CHECKLIST.md` covers the additional evidence for an official
Windows installer or a production-labelled release: clean-machine WebView2
behavior, offline installation, interactive upload/solve/cancellation, privacy
review, upgrade/rollback, SBOM review and optional Authenticode signing. A public
MIT source release or an unsigned free preview does not require a paid signing
certificate or a CakeLPR fixture.

### Native worker

Use [`native/SolverWorker/README.md`](native/SolverWorker/README.md) as the
canonical build guide. It defines the supported MSYS2 Windows release path and
the separate Linux-only development path; Visual Studio must not link the
MinGW-built CaDiCaL library.

Projects target .NET 8 LTS. The SDK pin is 9.0.312 because it is the SDK
currently installed on the development machine; CI must install that exact SDK
or deliberately update `global.json` in a reviewed dependency change.

### Formal verification tools

The generated CakeLPR checker source is under
`native/FormalVerification/third_party/cake_lpr`. Its pinned CakeML and HOL4
proof/compiler sources are Git submodules. Clone the repository with:

```bash
git clone --recurse-submodules <repository-url>
```

For an existing clone, initialize the pinned sources with:

```bash
git submodule sync --recursive
git submodule update --init --recursive
```

Build the optional Windows checker with:

```bash
cd scheduler/desktop
./scripts/build-cake-lpr.sh
```

The generated executable is ignored. A formal release must record its approved
Windows SHA-256 and pass that value to the exported verification script.
