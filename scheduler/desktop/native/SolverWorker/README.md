# SolverWorker

`SolverWorker` is the isolated native CaDiCaL process. It accepts exactly one NDJSON
request, enumerates bounded SAT models, and exits. C# owns workbook semantics, CNF
encoding, model validation, personal-timetable validation, and ranking.

The protocol is versioned and documented in `PROTOCOL.md`. The worker links the pinned
MIT-licensed CaDiCaL 3.0.1 source in `third_party/cadical`; provenance and notices are
in `third_party/`.

## Windows build

Use an **MSYS2 MINGW64 or UCRT64** shell with a current GCC package. The old
`C:\Program Files\MinGW` GCC 6.3 is unsupported because CaDiCaL 3.0.1 requires C++17.

```bash
# Run from the repository's scheduler/desktop directory.
cd scheduler/desktop
./scripts/build-windows-solver-worker.sh
```

The script builds the vendored CaDiCaL source and writes the verified executable to
`native/SolverWorker/out/windows-x64/SolverWorker.exe`. That directory is generated and
ignored. It runs `--version`, `--self-test`, and `--protocol-self-test` before succeeding.
The resulting worker statically links the MinGW runtime; the build fails if it still
depends on `libgcc`, `libstdc++`, or `libwinpthread` DLLs.

## Linux development build

```bash
cd native/SolverWorker/third_party/cadical
./configure
make -C build -j2 libcadical.a
cd ../../../..
cmake --preset linux-x64-release -S native/SolverWorker
cmake --build --preset linux-x64-release
ctest --preset linux-x64-release
```
