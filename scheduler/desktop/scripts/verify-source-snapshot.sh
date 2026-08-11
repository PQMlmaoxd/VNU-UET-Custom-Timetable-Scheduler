#!/usr/bin/env sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../../.." && pwd)
cd "$repository_root"

manifest_path=${SCHEDULER_SOURCE_SNAPSHOT_MANIFEST:-docs/release/SOURCE_SNAPSHOT.sha256}

if [ ! -f "$manifest_path" ]; then
  printf '%s\n' "$manifest_path is missing." >&2
  exit 1
fi

actual_manifest=$(mktemp)
expected_manifest=$(mktemp)
canonical_manifest=$(mktemp)
trap 'rm -f "$actual_manifest" "$expected_manifest" "$canonical_manifest"' EXIT HUP INT TERM

# Hash committed Git blobs so Windows checkout line-ending and symlink handling
# cannot change the source snapshot result.
git ls-files | while IFS= read -r path; do
  [ "$path" = "${manifest_path#./}" ] && continue
  printf '%s  ./%s\n' \
    "$(git cat-file blob "HEAD:$path" | sha256sum | awk '{ print $1 }')" \
    "$path"
done > "$actual_manifest"

LC_ALL=C sort "$manifest_path" > "$expected_manifest"
LC_ALL=C sort "$actual_manifest" > "$canonical_manifest"

if ! cmp -s "$expected_manifest" "$canonical_manifest"; then
  printf '%s\n' "Snapshot contents differ from $manifest_path." >&2
  diff -u "$expected_manifest" "$canonical_manifest" | sed -n '1,80p' >&2 || true
  exit 1
fi
