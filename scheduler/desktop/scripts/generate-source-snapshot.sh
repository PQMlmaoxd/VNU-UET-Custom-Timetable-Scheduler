#!/usr/bin/env bash
# Regenerate docs/release/SOURCE_SNAPSHOT.sha256 from the current Git index.
#
# The manifest hashes committed blob content (index entries), so Windows
# checkout line-ending handling cannot change the result. The manifest itself
# and mode-160000 gitlink entries are excluded, matching
# scheduler/desktop/scripts/verify-source-snapshot.sh.
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../../.." && pwd)
cd "$repository_root"

manifest_path=${SCHEDULER_SOURCE_SNAPSHOT_MANIFEST:-docs/release/SOURCE_SNAPSHOT.sha256}

tmp_manifest=$(mktemp)
trap 'rm -f "$tmp_manifest"' EXIT HUP INT TERM

git ls-files --stage | while IFS=$' \t' read -r mode object stage path; do
  [ "$mode" = "160000" ] && continue
  [ "$path" = "${manifest_path#./}" ] && continue
  printf '%s  ./%s\n' \
    "$(git cat-file blob "$object" | sha256sum | awk '{ print $1 }')" \
    "$path"
done | LC_ALL=C sort > "$tmp_manifest"

mv "$tmp_manifest" "$manifest_path"
printf '%s: regenerated %d entries.\n' "$manifest_path" "$(wc -l < "$manifest_path")"
