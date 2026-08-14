#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd -- "$(dirname -- "$0")/../../.." && pwd)
cd "$repository_root"

lock_file="scheduler/desktop/native/FormalVerification/cake-lpr-submodules.lock"
cake_root="scheduler/desktop/native/FormalVerification/third_party/cake_lpr"
vendored_manifest="scheduler/desktop/native/FormalVerification/cake-lpr-vendored-files.sha256"
require_initialized=0
gitlinks_only=0

usage() {
  cat <<'EOF'
Usage: verify-cake-lpr-provenance.sh [--gitlinks-only] [--require-initialized-submodules]

Checks the pinned CakeLPR source, its CakeML/HOL4 gitlinks, and project-owned
source hashes. It never fetches or changes submodules.
EOF
}

for argument in "$@"; do
  case "$argument" in
    --gitlinks-only) gitlinks_only=1 ;;
    --require-initialized-submodules) require_initialized=1 ;;
    --help|-h) usage; exit 0 ;;
    *) printf 'Unknown argument: %s\n' "$argument" >&2; usage >&2; exit 2 ;;
  esac
done

fail() {
  printf 'CakeLPR provenance error: %s\n' "$1" >&2
  exit 1
}

[[ -f .gitmodules ]] || fail 'root .gitmodules is missing.'
[[ -f "$lock_file" ]] || fail "$lock_file is missing."
[[ -d "$cake_root" ]] || fail "$cake_root is missing."

declare -A locked_commit=()
declare -A locked_url=()
declare -A locked_seen=()
lock_count=0

while read -r commit path url extra; do
  [[ -z "${commit:-}" || "$commit" == \#* ]] && continue
  [[ -z "${path:-}" || -n "${extra:-}" ]] && fail "Malformed lock entry: $commit $path $url"
  [[ "$commit" =~ ^[0-9a-f]{40}$ ]] || fail "Invalid submodule commit for $path."
  path=${path#./}
  [[ -z "${locked_seen[$path]+present}" ]] || fail "Duplicate submodule path in lock: $path"
  locked_seen["$path"]=1
  locked_commit["$path"]=$commit
  locked_url["$path"]=$url
  lock_count=$((lock_count + 1))
done < "$lock_file"

[[ "$lock_count" -gt 0 ]] || fail 'Submodule lock is empty.'

configured_count=0
configured_paths=()
while read -r key path; do
  [[ -z "${key:-}" ]] && continue
  module_name=${key#submodule.}
  module_name=${module_name%.path}
  module_url=$(git config --file .gitmodules --get "submodule.${module_name}.url" || true)
  [[ -n "$module_url" ]] || fail "No URL configured for submodule $path."
  path=${path#./}
  [[ -n "${locked_seen[$path]+present}" ]] || fail "Submodule $path is not in the lock file."
  [[ "${locked_url[$path]}" = "$module_url" ]] || fail "URL mismatch for $path."

  index_entry=$(git ls-files --stage -- "$path")
  index_mode=$(printf '%s\n' "$index_entry" | awk 'NR == 1 { print $1 }')
  index_commit=$(printf '%s\n' "$index_entry" | awk 'NR == 1 { print $2 }')
  [[ "$index_mode" = "160000" ]] || fail "$path is not a Git submodule gitlink."
  [[ "$index_commit" = "${locked_commit[$path]}" ]] || fail "Pinned commit mismatch for $path."

  configured_paths+=("$path")
  configured_count=$((configured_count + 1))

  if [[ "$require_initialized" -eq 1 ]]; then
    [[ -e "$path/.git" ]] || fail "$path is not initialized; clone with --recurse-submodules."
    actual_commit=$(git -C "$path" rev-parse --verify HEAD)
    [[ "$actual_commit" = "${locked_commit[$path]}" ]] || fail "Initialized commit mismatch for $path."
  fi
done < <(git config --file .gitmodules --get-regexp '^submodule\..*\.path$')

[[ "$configured_count" -eq "$lock_count" ]] || fail 'The .gitmodules and submodule lock entry counts differ.'

if [[ "$gitlinks_only" -eq 1 ]]; then
  printf 'CakeLPR gitlinks verified: %s pinned submodules.\n' "$configured_count"
  exit 0
fi

for required_file in basis_ffi.c cake_lpr.S cake_lpr_arm8.S LICENSE; do
  [[ -s "$cake_root/$required_file" ]] || fail "Missing CakeLPR source file: $required_file"
done

[[ -f "$vendored_manifest" ]] || fail "$vendored_manifest is missing."
if ! (cd "$cake_root" && sha256sum -c -- "$repository_root/$vendored_manifest"); then
  fail 'Project-owned CakeLPR source hashes do not match.'
fi

if [[ "$require_initialized" -eq 1 ]]; then
  for required_file in \
    "$cake_root/cakeml/LICENSE" \
    "$cake_root/cakeml/examples/lpr_checker/lprScript.sml" \
    "$cake_root/cakeml/examples/lpr_checker/lpr_parsingScript.sml" \
    "$cake_root/HOL/COPYRIGHT" \
    "$cake_root/HOL/bin/README"; do
    [[ -s "$required_file" ]] || fail "Missing initialized proof source: $required_file"
  done
fi

printf 'CakeLPR provenance verified: generated source hashes and %s pinned submodules.\n' "$configured_count"
