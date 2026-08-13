# Scheduler Diagnostics CLI

`Scheduler.Diagnostics` is a separate managed console project for offline Windows
support checks. It targets `net8.0` with the `win-x64` runtime and does not reference
WPF, WebView2, or `Scheduler.Desktop`.

## Commands

Run from `scheduler/desktop`:

```powershell
dotnet run --project src/Scheduler.Diagnostics -- help
dotnet run --project src/Scheduler.Diagnostics -- version
dotnet run --project src/Scheduler.Diagnostics -- self-test
dotnet run --project src/Scheduler.Diagnostics -- worker --worker .\native\SolverWorker\out\windows-x64\SolverWorker.exe
dotnet run --project src/Scheduler.Diagnostics -- app --app .\artifacts\windows-x64
dotnet run --project src/Scheduler.Diagnostics -- workbook --workbook C:\private\input.xlsx
dotnet run --project src/Scheduler.Diagnostics -- doctor --worker .\native\SolverWorker\out\windows-x64\SolverWorker.exe
```

`worker` uses only the native worker's fixed version/self-test flags and two embedded
one-variable protocol cases: one known SAT request and one known UNSAT request. It
does not accept arbitrary SAT/CNF input. Process output and wait time are bounded.

`app` inspects package files and `release-manifest.json` without starting the desktop
executable or opening the UI. `workbook` uses the existing bounded XLSX or signed
text-native PDF parser. Unsupported or scanned PDF layouts are rejected rather than
parsed partially.

## Output and privacy

Text is the default. Use `--format json` for the versioned schema (`schema_version: 1`),
and `--output <file>` to write the same report to a file as well as standard output.
The default report does not include usernames, machine names, environment values,
absolute paths, workbook filenames, course/LHP/lecturer/room/cohort values, or raw
parser warnings.

These are explicit private opt-ins:

```powershell
--include-paths
--include-file-hashes
--verbose-private
```

Exit codes are stable: `0` all checks passed, `1` a check failed or is unsupported,
`2` usage error, `4` a requested target is missing, and `5` an internal/reporting
error. `doctor` always runs the managed self-test and runs each optional target check
provided by the caller.

## Windows publish

The standalone artifact is self-contained, single-file, untrimmed `win-x64` output:

```powershell
.\scripts\publish-windows-diagnostics.ps1
```

The default output is `artifacts/diagnostics-windows-x64/Scheduler.Diagnostics.exe`.
The script smoke-tests `help`, the requested `version`, `self-test`, and versioned
JSON `doctor` output. In the release workflow it also runs the CLI against the
just-built worker and desktop publish directory. Running it locally creates only the
standalone diagnostics directory. The tagged release workflow runs it after the release gate, then
`package-release-assets.ps1` copies the exact executable into the third release asset:
`VNU-UET-Custom-Timetable-Scheduler-<version>-Diagnostics-win-x64.exe`.

To exercise the same packaging handoff locally after producing the desktop publish
directory and installer, pass the executable path, not its containing directory:

```powershell
.\scripts\package-release-assets.ps1 `
  -Version 0.1.2 `
  -PublishPath .\artifacts\windows-x64 `
  -InstallerPath .\artifacts\installer\VNU-UET-Custom-Timetable-Scheduler-0.1.2-Setup.exe `
  -DiagnosticsPath .\artifacts\diagnostics-windows-x64\Scheduler.Diagnostics.exe
```

## Verification

Targeted commands on a Windows machine with the SDK pinned by `global.json`:

```powershell
dotnet restore Scheduler.sln --locked-mode
dotnet build src/Scheduler.Diagnostics/Scheduler.Diagnostics.csproj --configuration Release --no-restore
dotnet test tests/Scheduler.Diagnostics.Tests/Scheduler.Diagnostics.Tests.csproj --configuration Release --no-restore
```

The native integration test is skipped unless `SCHEDULER_SOLVER_WORKER` points to a
built worker. The CLI itself never reads that environment variable.
