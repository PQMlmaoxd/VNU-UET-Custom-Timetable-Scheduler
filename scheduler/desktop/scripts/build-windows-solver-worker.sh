#!/usr/bin/env bash
set -euo pipefail

if [[ "${MSYSTEM:-}" != MINGW64 && "${MSYSTEM:-}" != UCRT64 ]]; then
  echo "Run this script from an MSYS2 MINGW64 or UCRT64 shell, not WSL or cmd.exe." >&2
  exit 2
fi

if ! printf '#include <variant>\nint main() {}\n' | g++ -std=c++17 -x c++ - -o /tmp/scheduler-cxx17-check.exe; then
  echo "A current C++17 MinGW-w64 compiler is required. GCC 6.3 is unsupported." >&2
  exit 2
fi

if command -v make >/dev/null 2>&1; then
  make_command=make
elif command -v mingw32-make >/dev/null 2>&1; then
  make_command=mingw32-make
else
  echo "GNU Make is required (make or mingw32-make)." >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
desktop_dir="$(cd "${script_dir}/.." && pwd)"
worker_dir="${desktop_dir}/native/SolverWorker"
cadical_dir="${worker_dir}/third_party/cadical"
output_dir="${worker_dir}/out/windows-x64"

if [[ ! -f "${cadical_dir}/src/cadical.hpp" ]]; then
  echo "Pinned CaDiCaL source is missing at ${cadical_dir}." >&2
  exit 2
fi

rm -rf "${cadical_dir}/build"
(
  cd "${cadical_dir}"
  ./configure
  "${make_command}" -C build -j"${NUMBER_OF_PROCESSORS:-2}" libcadical.a
)

mkdir -p "${output_dir}"
g++ \
  -std=c++20 \
  -Wall -Wextra -Wpedantic -Werror \
  -static \
  -static-libgcc -static-libstdc++ \
  -I "${cadical_dir}/src" \
  "${worker_dir}/src/main.cpp" \
  "${worker_dir}/src/protocol.cpp" \
  "${cadical_dir}/build/libcadical.a" \
  -lpsapi \
  -o "${output_dir}/SolverWorker.exe"

if objdump -p "${output_dir}/SolverWorker.exe" | grep -Eqi 'DLL Name: lib(gcc|stdc\+\+|winpthread)'; then
  echo "SolverWorker.exe must not depend on MinGW runtime DLLs." >&2
  exit 1
fi

"${output_dir}/SolverWorker.exe" --version
"${output_dir}/SolverWorker.exe" --self-test
"${output_dir}/SolverWorker.exe" --protocol-self-test
