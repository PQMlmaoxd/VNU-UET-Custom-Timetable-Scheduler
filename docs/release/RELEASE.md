# Release Guide

The release pipeline produces a self-contained Windows x64 application and a
standalone diagnostics CLI, not a server deployment. The public source repository
contains C#/.NET, React and the native SolverWorker source. Installers and compiled
binaries are published as GitHub Release assets.

## One-Time GitHub Setup

1. Create an empty GitHub repository.
2. Push the reviewed contents of `github_src_code` to the `main` branch:

   ```powershell
   cd C:\path\to\github_src_code
   git init -b main
   git add .
   git commit -m "Initial public release"
   git remote add origin https://github.com/<owner>/<repository>.git
   git push -u origin main
   ```

3. Enable Actions for the repository.
4. Add these repository variables under **Settings > Secrets and variables > Actions > Variables**:

   - `WEBVIEW2_INSTALLER_URL`: official Microsoft Evergreen Standalone Runtime URL for Windows x64.
   - `WEBVIEW2_INSTALLER_SHA256`: SHA-256 of that exact installer.

The variables are not treated as a trust root by themselves. The workflow also
checks the HTTPS host, SHA-256 and Authenticode signature. The runtime type and
the source URL still require a release-owner review before publication.

## Pull Request and Main CI

`.github/workflows/windows-desktop.yml` runs on relevant pull requests, pushes
to `main`, and manual dispatches. It performs the following checks in a fixed
Windows 2022 environment:

1. Verify `docs/release/SOURCE_SNAPSHOT.sha256` when the manifest is present.
2. Install .NET SDK `9.0.312` and Node.js `24.12.0`.
3. Install the MSYS2 MINGW64 native toolchain and build the pinned CaDiCaL worker.
4. Build the pinned optional CakeLPR checker source.
5. Run locked NuGet restore (`--locked-mode`).
6. Build and run the fixture-independent .NET tests with warnings treated as errors.
7. Run frontend unit tests, production build and Playwright E2E tests.
8. Publish a self-contained `win-x64` directory and generate the SBOM/release manifest.
9. Publish and smoke-test the standalone `Scheduler.Diagnostics.exe` single-file CLI.
10. Upload the verified desktop publish directory and diagnostics executable as short-lived Actions artifacts.

The public CI and release workflow do not contain private timetable fixtures.
They pass `-SkipExternalFixtureTests` to exclude the compatibility and
`ExternalFixture` categories that require those files. A protected local release run must provide both
`SCHEDULER_TEST_WORKBOOK` and `SCHEDULER_TEST_PDF` and use
`-RequireExternalFixtures` so those tests are executed.

## Local Release Gate

Run from `scheduler/desktop` in a Windows PowerShell session after building the
worker with the supported MSYS2 toolchain:

```powershell
$env:SCHEDULER_TEST_WORKBOOK = 'C:\path\to\approved\timetable.xlsx'
$env:SCHEDULER_TEST_PDF = 'C:\path\to\approved\timetable.pdf'

powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\verify-windows-release.ps1 `
  -SolverWorkerPath .\native\SolverWorker\out\windows-x64\SolverWorker.exe `
  -ReleaseVersion 1.0.0 `
  -RequireExternalFixtures
```

Close any running packaged application before publishing. Windows locks loaded
assemblies and can make an otherwise valid publish fail.

## Release By Tag

The release workflow is `.github/workflows/release-windows.yml`. A release is
created by pushing an annotated semantic-version tag:

```powershell
git tag -a v1.0.0 -m "Release v1.0.0"
git push origin main
git push origin v1.0.0
```

The workflow derives the application version from the tag and refuses malformed
versions. It then:

1. Verifies the source snapshot and builds the native worker.
2. Runs the complete locked CI/release gate with the same version.
3. Publishes and smoke-tests the standalone diagnostics CLI after the release gate.
4. Downloads and verifies the configured WebView2 runtime.
5. Installs and version-checks Inno Setup 6.7.1.
6. Builds the installer and runs silent install, worker and uninstall smoke tests.
7. Creates the self-contained ZIP, installer, and diagnostics executable. The release manifest, SBOM and
    third-party notices remain inside the published tree and ZIP.
8. Creates the GitHub Release and uploads all three product assets using
    `GITHUB_TOKEN`; GitHub also provides source-code ZIP and tar archives.

The release job fails intentionally when the WebView2 variables are missing,
when any version/hash check fails, when tests fail, or when the installer smoke
test fails. It does not silently publish a partial installer.

The source repository and unsigned preview builds remain free and open source.
The WebView2 variables are required only by this tagged installer workflow,
which is configured to produce an offline-capable installer. Authenticode
signing and CakeLPR evidence are release-hardening options, not application
runtime requirements.

A manual release can be started from **Actions > Release Windows Desktop > Run
workflow** by supplying `version`, `webview2_installer_url` and
`webview2_sha256`. Manual input values override repository variables.

## Release Assets

The GitHub Release contains these uploaded assets:

- `VNU-UET-Custom-Timetable-Scheduler-<version>-Setup.exe`: per-user Inno Setup installer.
- `VNU-UET-Custom-Timetable-Scheduler-<version>-win-x64.zip`: self-contained publish directory.
- `VNU-UET-Custom-Timetable-Scheduler-<version>-Diagnostics-win-x64.exe`: self-contained, single-file diagnostics CLI.

The diagnostics asset is built from `Scheduler.Diagnostics`, smoke-tested with
`help`, `version`, `self-test`, and JSON `doctor`, and is copied only when the
input file is named exactly `Scheduler.Diagnostics.exe`. Its CLI commands and
privacy defaults are documented in
`scheduler/desktop/src/Scheduler.Diagnostics/README.md`.

GitHub additionally provides `Source code (zip)` and `Source code (tar.gz)` for
the release tag. The ZIP itself retains `release-manifest.json`, `sbom.cdx.json`
and `THIRD_PARTY_NOTICES.md` from the publish directory.

## Manual Acceptance For An Official Installer

CI cannot replace these release-owner checks when publishing a production-labelled
installer or a formal-verification claim:

- clean Windows machine without a preinstalled WebView2 Runtime;
- interactive WebView2 startup, upload, solve and cancellation behavior;
- Authenticode signing and verification of the installer and binaries, if signed
  distribution is desired;
- an authorized CaDiCaL-to-CakeLPR Windows proof fixture, only when publishing
  a formal UNSAT verification claim;
- legal/privacy approval for external timetable fixtures and template-specific PDF aliases.

Do not describe an installer as production-approved until the acceptance
checklist at `scheduler/desktop/RELEASE_ACCEPTANCE_CHECKLIST.md` is signed off.
