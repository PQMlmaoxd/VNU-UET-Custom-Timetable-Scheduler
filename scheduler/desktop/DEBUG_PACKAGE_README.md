# Windows Debug Solver Package

This package is for diagnosing solver failures on another Windows machine. It is
not a production release and is not code-signed.

## Run the direct worker checks first

Keep every file in this directory together. Open PowerShell in this directory and
run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\run-solver-diagnostics.ps1 -RootPath $PWD -FailOnCheckFailure
```

The check runs the native worker independently of the WPF/WebView2 interface:

- worker version, self-test and protocol self-test;
- one known SAT request, which must return one model;
- one known UNSAT request, which must return no models;
- executable path, SHA-256, architecture and Authenticode status;
- relevant solver environment variables and stdout/stderr.

The result is written to `solver-diagnostics.json`. Review and redact usernames,
local paths and environment values before sharing it. Do not put timetable files
or private workbook/PDF data in this package.

If a check fails, also inspect Windows Security Protection History and Event Viewer
for AppLocker, Code Integrity, WDAC, Defender or EDR events at the same time.

## Start the desktop host

Only after the direct checks pass, run:

```powershell
.\Scheduler.Desktop.exe
```

The desktop host requires the Microsoft WebView2 Runtime. If the direct checks
pass but the UI cannot solve, the problem is in host discovery, WebView2, policy,
or the bridge rather than the native SAT worker itself.

This package targets 64-bit Windows (`win-x64`). Do not copy only the executable;
the `web` directory and `SolverWorker.exe` beside it are required.
