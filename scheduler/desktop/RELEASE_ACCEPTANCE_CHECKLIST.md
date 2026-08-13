# Windows Desktop Release Acceptance Checklist

Use this checklist for every preview or production release after the automated
gate succeeds. It records evidence that cannot be inferred from a build, unit
test, or installer smoke test. Do not mark a release ready when a required item
is unverified; record the blocker and its owner instead.

## 1. Release Inputs

- [ ] Release version follows `major.minor.patch` and is identical in the published
      assembly, installer filename, release manifest, and SBOM metadata.
- [ ] The exact `SolverWorker.exe` used for the release passed `--version`,
      `--self-test`, and `--protocol-self-test`.
- [ ] The standalone `Scheduler.Diagnostics.exe` passed its publish smoke tests and
      is recorded under the exact product-qualified diagnostics asset name.
- [ ] CaDiCaL source provenance remains `rel-3.0.1`, commit
      `c60730422e758ef1cebe7aeddf2dda31c996bf04`; its notice and license are in
      the published directory and installer.
- [ ] `sbom.cdx.json` was reviewed for unexpected packages, known vulnerability
      advisories, and license obligations. Record the review tool, database date,
      reviewer, and findings outside this repository if they contain sensitive
      operational information.
- [ ] If the installer bundles WebView2, its source is the official Evergreen
      **Standalone** Runtime, its HTTPS source URL and SHA-256 were independently
      reviewed, and the bootstrapper is not used as an offline dependency.
- [ ] A signing decision is recorded. Unsigned preview builds must be labelled as
       such; production builds require the approved signing policy and verification
       of the published signature.
- [ ] The release record identifies the source commit, CI run, GitHub artifact
       digest, Windows runner image, MSYS2 package versions, and Inno Setup version.
       `windows-latest` and action tags are convenience inputs, not immutable
       production provenance.

## 2. Automated Evidence

Run from `scheduler/desktop` on the release build machine. Preserve the console
output and artifact hashes with the release record, never in the support ZIP.

```powershell
.\scripts\verify-windows-release.ps1 `
  -SolverWorkerPath .\native\SolverWorker\out\windows-x64\SolverWorker.exe `
  -BuildInstaller `
   -ReleaseVersion <release-version> `
   -InstallerVersion <release-version> `
   -RequireExternalFixtures `
   -InstallerSmokeTest
```

- [ ] The command exits successfully with no build warnings or test failures.
- [ ] Native integration tests ran with `SCHEDULER_SOLVER_WORKER` set by the script;
       they were not skipped. `SCHEDULER_TEST_WORKBOOK` and `SCHEDULER_TEST_PDF`
       identify the authorized protected fixtures when `-RequireExternalFixtures` is used.
- [ ] React tests and production bundle build passed.
- [ ] Publish directory contains `Scheduler.Desktop.exe`, `SolverWorker.exe`,
      `web/index.html`, `release-manifest.json`, `THIRD_PARTY_NOTICES.md`, and
      `sbom.cdx.json`.
- [ ] Installer silent install/uninstall smoke test passed.
- [ ] Diagnostics `help`, `version`, `self-test`, and JSON `doctor` smoke tests passed;
       the CLI asset is self-contained `win-x64` output.

## 3. Clean-Machine Installation

Use a disposable Windows x64 VM or physical machine with no prior Scheduler
Desktop installation. Record Windows edition/build, CPU architecture, available
disk, WebView2 state, and installer SHA-256.

- [ ] Verify the machine has no `VNU-UET Custom Timetable Scheduler` installation under
      `%LOCALAPPDATA%\Programs` and no existing local activity logs.
- [ ] Test the installer without WebView2. It must stop with the actionable runtime
      message and leave no partial application installation.
- [ ] Install the approved WebView2 Runtime using the documented release input.
- [ ] Run the installer normally. It must install without administrator elevation
      and create the requested Start Menu/desktop shortcuts only.
- [ ] Disconnect all network interfaces. Start the installed application and
      confirm it opens without network access or a local HTTP listener.
- [ ] Uninstall and confirm the application directory, shortcuts, and uninstall
      entry are removed. Local activity logs may remain by design and must be
      documented to the tester.

## 4. Interactive Product Smoke

Use an approved anonymized workbook fixture or an authorized real workbook. Do
not attach institutional workbooks, screenshots containing student data, or
support bundles to source control.

- [ ] Open the installed application and confirm `Trợ giúp > Thông tin kỹ thuật`
      displays the release version, CaDiCaL version/commit, bridge protocol, and
      detected WebView2 runtime without showing workbook data.
- [ ] Upload a valid workbook, inspect fixed-schedule validation findings, and
      select a valid `(course_code, teaching_team_key)` request.
- [ ] Solve successfully and verify each returned teaching unit has the requested
      course and teaching team, no duplicated selection, and the displayed movement
      cost matches the known fixture expectation.
- [ ] With an authorized UNSAT fixture, use `Xuất gói kiểm chứng UNSAT`, run the
      package's `verify-unsat.ps1` with the approved CaDiCaL and `cake_lpr` binaries,
      and record the exact `s VERIFIED UNSAT` output plus both executable hashes.
      The checker certifies only the exact CNF and proof hashes supplied from the
      export custody record; it
      does not certify the timetable parser, encoder, or timetable semantics.
      Do not mark an export without that checker result as formally verified.
- [ ] Use `Dừng solver` while a deliberately long request is active. The UI must
      return to selection, show cancellation rather than a generic failure, and
      leave no worker process running.
- [ ] Upload an invalid or wrong-sheet XLSX. The UI must show a clear error and
      remain usable for the next upload.
- [ ] Confirm the packaged host uses the local WebView bridge and never asks for
      a server path or starts an HTTP listener.

## 5. Privacy and Support Bundle

- [ ] Trigger `Trợ giúp > Xuất gói hỗ trợ` and inspect the ZIP before sharing it.
- [ ] ZIP contains only `README.txt`, `diagnostics.json`, and `activity.jsonl`.
- [ ] ZIP contains no workbook names/content, filesystem paths, selected teaching-team
      requests,
      request payloads, correlation IDs, or exception details.
- [ ] The support ticket includes the user-approved ZIP and reproduction steps,
      never an unreviewed workbook.

## 6. Upgrade and Rollback

- [ ] Install the prior approved desktop release, then install this release over
      it. Confirm launch, validation, solve, diagnostics, and uninstall work.
- [ ] Keep the prior approved installer and its hashes available for rollback.
- [ ] Record the cutover decision and confirm that the public release contains
      only the C# desktop runtime and the bundled frontend.

## Sign-Off Record

| Field | Evidence |
| --- | --- |
| Release version and artifact SHA-256 | |
| Automated gate log location | |
| SBOM/license/vulnerability reviewer | |
| Clean-machine tester and environment | |
| Interactive smoke tester and fixture authorization | |
| Upgrade/rollback tester | |
| Signing decision | |
| Open exceptions, owner, expiry | |
