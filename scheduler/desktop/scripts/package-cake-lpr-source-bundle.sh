#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 1 ]]; then
  echo "Usage: package-cake-lpr-source-bundle.sh <output-tar-gz>" >&2
  exit 2
fi

repository_root=$(cd -- "$(dirname -- "$0")/../../.." && pwd)
cd "$repository_root"

output_path=$1
output_directory=$(dirname -- "$output_path")
mkdir -p "$output_directory"

"$repository_root/scheduler/desktop/scripts/verify-cake-lpr-provenance.sh" \
  --require-initialized-submodules

staging_directory=$(mktemp -d)
temporary_tar=$(mktemp --suffix=.tar)
trap 'rm -rf "$staging_directory" "$temporary_tar"' EXIT HUP INT TERM

git archive --format=tar HEAD | tar -xf - -C "$staging_directory"

while read -r commit path url extra; do
  [[ -z "${commit:-}" || "$commit" == \#* ]] && continue
  path=${path#./}
  submodule_directory="$repository_root/$path"
  destination_directory="$staging_directory/$path"
  mkdir -p "$destination_directory"
  git -C "$submodule_directory" archive --format=tar HEAD | tar -xf - -C "$destination_directory"
done < "$repository_root/scheduler/desktop/native/FormalVerification/cake-lpr-submodules.lock"

for required_file in \
  "$staging_directory/scheduler/desktop/native/FormalVerification/third_party/cake_lpr/cakeml/examples/lpr_checker/lprScript.sml" \
  "$staging_directory/scheduler/desktop/native/FormalVerification/third_party/cake_lpr/HOL/COPYRIGHT"; do
  [[ -s "$required_file" ]] || {
    echo "Recursive source bundle is missing $required_file" >&2
    exit 1
  }
done

if find "$staging_directory" -type d -name .git -print -quit | grep -q .; then
  echo 'Recursive source bundle must not contain .git directories.' >&2
  exit 1
fi

SOURCE_DATE_EPOCH=0 tar -cf "$temporary_tar" \
  --sort=name \
  --mtime='UTC 1970-01-01' \
  --owner=0 \
  --group=0 \
  --numeric-owner \
  -C "$staging_directory" .
gzip -n -9 "$temporary_tar"
mv "$temporary_tar.gz" "$output_path"

printf 'Recursive CakeML/HOL4 source bundle created: %s\n' "$output_path"
sha256sum "$output_path"
