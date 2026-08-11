# Public Source Snapshot

The public source export is generated from the reviewed workspace and verified
before publication. The workspace has no Git commit until the public repository
is initialized, so the export must be tied to a reviewed commit before release.

The export contains the C#/.NET desktop application, React frontend, native
SolverWorker source, the complete pinned CakeLPR source, formal specifications,
tests, locked dependency graphs and release tooling. It excludes Python/backend
source, local timetable files, generated PDFs, proof packages, binaries, caches,
installers and agent tooling.

`SOURCE_SNAPSHOT.sha256` records every regular file and symlink in the export.
From the public repository root, verify it with:

```sh
./scheduler/desktop/scripts/verify-source-snapshot.sh
```

Desktop installers and signed binaries belong in GitHub Release assets, not in
the source repository.
