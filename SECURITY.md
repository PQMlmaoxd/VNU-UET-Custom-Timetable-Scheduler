# Security Policy

## Supported Versions

Only the current source tree and the most recent approved desktop release are
actively considered for security fixes.

## Reporting

Do not report a vulnerability with a public issue when it includes a timetable,
lecturer identity, local path, log, proof package, or executable. Use a private
maintainer channel configured for the GitHub repository. If no private channel
has been configured yet, remove sensitive data and request one through a
non-public organizational channel.

Include the affected version or commit, operating system, reproduction steps,
and a minimal redacted fixture. Never attach the real institutional workbook or
PDF.

## Security Boundaries

- Timetable input is treated as untrusted data.
- Native solver execution is isolated in a child process.
- Desktop uploads are size-limited and checked by extension and file signature.
- Formal proof scripts verify the formula hash before invoking `cake_lpr`.
- Support bundles are sanitized, but users must review them before sharing.
