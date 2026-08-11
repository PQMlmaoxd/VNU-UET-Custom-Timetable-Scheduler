# Data Handling

The application processes timetable files locally. The normal desktop flow
does not require a network connection or a remote server.

## Local Data

- Uploaded XLSX/PDF bytes are written to a temporary per-command file and
  deleted after parsing or solving.
- Activity logs contain command metadata and timing only. They do not contain
  workbook bytes, filenames, paths, selections, payloads, or exception details.
- Support ZIPs contain sanitized diagnostics and activity records. Inspect a
  ZIP before sharing it.
- Formal UNSAT packages are different from support ZIPs. They intentionally
  contain course, lecturer, LHP, session, and formula metadata needed for
  auditability.
- The PDF importer includes a small, template-specific lecturer alias table to
  correct clipped names in the signed public timetable layout. Maintain that
  table only after confirming that the identities are public institutional data
  and approved for source distribution. Do not add aliases derived from private
  documents or infer names from arbitrary prefixes.

## Test Data

The real institutional workbook and signed PDF used for local compatibility
checks remain outside source control. Tests skip those checks when the files
are unavailable. Do not copy them into the repository, CI artifacts, issue
attachments, screenshots, or support tickets.

Release records should store test counts, tool versions, and artifact hashes,
not raw timetable files or local filesystem paths. Access to authorized real
data is limited to the release tester and should follow the institution's
retention policy.

## Reporting

Do not publish timetable files, personal data, proof packages, or local logs in
an issue. For a suspected security issue, follow [`../../SECURITY.md`](../../SECURITY.md)
and provide a redacted reproduction.
