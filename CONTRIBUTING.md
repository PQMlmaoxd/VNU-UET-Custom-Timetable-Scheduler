# Contributing

## Scope

Keep the fixed-timetable personal-selection semantics unchanged unless the
formal specification, compatibility tests, and release notes are updated in
the same change. This product is not a global timetable rescheduler.

The C# desktop application is the supported product runtime. Python is not part
of the product source and must not be reintroduced as a runtime dependency.

## Change Rules

- Keep domain, parser, validation, solver, and transport concerns separated.
- Use the canonical C# CNF encoder for desktop artifacts and solver requests.
- Do not commit workbooks, PDFs, generated binaries, installers, caches, or
  local logs.
- Preserve the WebView bridge contract unless the change is explicitly versioned
  and tested.
- Keep user-facing copy free of internal implementation terms.
- Add a focused regression test for behavior changes and update the formal
  specification when hard constraints change.

## Required Checks

For frontend changes:

```powershell
cd scheduler/frontend
# Node.js 20.19+ is required; CI uses Node.js 24.12.0.
npm.cmd test
npm.cmd run build
npm.cmd run test:e2e
```

For desktop changes, build the native worker first and run
`scheduler/desktop/scripts/verify-windows-release.ps1`. Do not treat skipped
real-data or native integration tests as passing evidence.

Review generated diffs before committing. The repository intentionally keeps
release outputs and local source data out of Git.

The GitHub CI and release workflow is documented in
[`docs/release/RELEASE.md`](docs/release/RELEASE.md).
