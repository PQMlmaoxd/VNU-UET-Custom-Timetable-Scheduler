#!/usr/bin/env bash
set -euo pipefail

if [[ "${MSYSTEM:-}" != MINGW64 && "${MSYSTEM:-}" != UCRT64 ]]; then
  echo "Run this script from an MSYS2 MINGW64 or UCRT64 shell." >&2
  exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
desktop_dir="$(cd "${script_dir}/.." && pwd)"
source_dir="${desktop_dir}/native/FormalVerification/third_party/cake_lpr"
output_dir="${desktop_dir}/native/FormalVerification/out/windows-x64"
output_path="${output_dir}/cake_lpr.exe"

for required_file in basis_ffi.c cake_lpr.S LICENSE; do
  if [[ ! -f "${source_dir}/${required_file}" ]]; then
    echo "CakeLPR source is incomplete; missing ${required_file}." >&2
    exit 2
  fi
done

mkdir -p "${output_dir}"
gcc \
  -O2 \
  -std=c99 \
  -static \
  -static-libgcc \
  "${source_dir}/basis_ffi.c" \
  "${source_dir}/cake_lpr.S" \
  -o "${output_path}"

if objdump -p "${output_path}" | grep -Eqi 'DLL Name: lib(gcc|stdc\+\+|winpthread)'; then
  echo "cake_lpr.exe must not depend on MinGW runtime DLLs." >&2
  exit 1
fi

if ! "${output_path}" --CML_HEAP_SIZE=64 --CML_STACK_SIZE=64 2>&1 | grep -q 'Usage:'; then
  echo "cake_lpr.exe did not print its expected usage output." >&2
  exit 1
fi

positive_output="$(${output_path} --CML_HEAP_SIZE=64 --CML_STACK_SIZE=64 \
  "${source_dir}/example.cnf" "${source_dir}/example.lpr" 2>&1)"
if ! grep -Fqx 's VERIFIED UNSAT' <<< "${positive_output}"; then
  echo "CakeLPR did not verify its pinned upstream example." >&2
  printf '%s\n' "${positive_output}" >&2
  exit 1
fi

temporary_proof="$(mktemp)"
trap 'rm -f "${temporary_proof}"' EXIT
cp "${source_dir}/example.lpr" "${temporary_proof}"
printf '\n0\n' >> "${temporary_proof}"
set +e
negative_output="$(${output_path} --CML_HEAP_SIZE=64 --CML_STACK_SIZE=64 \
  "${source_dir}/example.cnf" "${temporary_proof}" 2>&1)"
negative_status=$?
set -e
if [[ "${negative_status}" -eq 0 ]] && grep -Fqx 's VERIFIED UNSAT' <<< "${negative_output}"; then
  echo "CakeLPR accepted a tampered proof." >&2
  exit 1
fi

sha256sum "${output_path}" > "${output_path}.sha256"
echo "CakeLPR candidate created: ${output_path}"
cat "${output_path}.sha256"
