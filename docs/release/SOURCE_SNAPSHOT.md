# Public Source Snapshot

The public source export is generated from the reviewed workspace and verified
before publication. The workspace has no Git commit until the public repository
is initialized, so the export must be tied to a reviewed commit before release.

The export contains the C#/.NET desktop application, React frontend, native
SolverWorker source, generated CakeLPR checker source, formal specifications,
tests, locked dependency graphs and release tooling. The pinned CakeML and HOL4
sources are represented by Git submodule commits and are not embedded in the
normal superproject archive. Clone with `--recurse-submodules` to obtain them.
It excludes Python/backend source, local timetable files, generated PDFs, proof
packages, binaries, caches, installers and agent tooling.

`SOURCE_SNAPSHOT.sha256` records every regular file and symlink in the export.
`cake-lpr-submodules.lock` records the expected submodule commit and URL. The
source snapshot verifier checks Gitlink entries separately because a Gitlink is
not a file blob.
From the public repository root, verify it with:

```sh
./scheduler/desktop/scripts/verify-source-snapshot.sh
```

Desktop installers and signed binaries belong in GitHub Release assets, not in
the source repository.
