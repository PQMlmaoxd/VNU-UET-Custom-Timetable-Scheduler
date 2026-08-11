# Formal Verification Tools

This directory contains the source and provenance for the optional UNSAT proof
toolchain. It is separate from the normal timetable-solving path.

The product uses the following sequence when a user requests a formal UNSAT
check:

1. The C# application exports the canonical `formula.cnf`.
2. The pinned CaDiCaL CLI creates an LRAT proof.
3. `cake_lpr` checks the exact CNF and proof.
4. The result is accepted only when the checker exits successfully and emits
   `s VERIFIED UNSAT`.

`third_party/cake_lpr` is the complete upstream source tree pinned by
`cake-lpr-provenance.json`. The upstream license and build files are retained.
The generated Windows executable is not committed to source; release builds
must record its SHA-256 in the approved tool record before calling it formally
verified.

## Windows build

Run from an MSYS2 MINGW64 or UCRT64 shell:

```bash
cd scheduler/desktop
./scripts/build-cake-lpr.sh
```

The script writes the generated executable and its local checksum under the
ignored `native/FormalVerification/out/windows-x64/` directory. It also checks
that the executable starts and that the output does not depend on the dynamic
MinGW runtime DLLs.

The build result is a candidate tool. It becomes an approved formal checker only
after the pinned Windows fixture, negative tests, binary hash and license review
are recorded.
