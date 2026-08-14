#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd -- "$(dirname -- "$0")/../../../../../.." && pwd)
lock_file="$repository_root/scheduler/desktop/native/FormalVerification/cake-lpr-submodules.lock"
cake_root="$repository_root/scheduler/desktop/native/FormalVerification/third_party/cake_lpr"

usage() {
  cat <<'EOF'
Usage: update.sh <CakeML_commit> <HOL4_commit>

Updates the two pinned proof-source submodules to explicit commits. This script
never follows a remote branch and never changes generated CakeLPR assembly.
Review the resulting diff and regenerate cake-lpr-vendored-files.sha256 only
after intentionally updating the generated checker sources.
EOF
}

if [[ "$#" -eq 1 && ("$1" = "--help" || "$1" = "-h") ]]; then
  usage
  exit 0
fi

if [[ "$#" -ne 2 ]]; then
  usage >&2
  exit 2
fi

[[ -f "$repository_root/.git" || -d "$repository_root/.git" ]] || {
  echo 'Run this script from a checked-out superproject.' >&2
  exit 2
}

declare -A requested_commit=(
  [cakeml]="$1"
  [HOL]="$2"
)

for module in cakeml HOL; do
  path="$cake_root/$module"
  commit=${requested_commit[$module]}
  [[ "$commit" =~ ^[0-9a-f]{40}$ ]] || {
    echo "Invalid commit: $commit" >&2
    exit 2
  }
  git -C "$path" fetch --no-tags origin "$commit"
  git -C "$path" checkout --detach "$commit"
done

temporary_lock=$(mktemp)
trap 'rm -f "$temporary_lock"' EXIT
while read -r commit path url extra; do
  [[ -z "${commit:-}" || "$commit" == \#* ]] && continue
  case "$path" in
    ./scheduler/desktop/native/FormalVerification/third_party/cake_lpr/cakeml)
      commit=${requested_commit[cakeml]}
      ;;
    ./scheduler/desktop/native/FormalVerification/third_party/cake_lpr/HOL)
      commit=${requested_commit[HOL]}
      ;;
  esac
  printf '%s %s %s\n' "$commit" "$path" "$url"
done < "$lock_file" > "$temporary_lock"
mv "$temporary_lock" "$lock_file"
trap - EXIT

git -C "$repository_root" add "$lock_file" "$cake_root/cakeml" "$cake_root/HOL"
echo 'Pinned CakeML and HOL4 submodules. Review and commit the staged gitlinks.'
