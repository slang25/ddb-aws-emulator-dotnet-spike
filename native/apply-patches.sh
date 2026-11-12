#!/usr/bin/env bash
# Applies native/patches/*.patch to the aws-emulator submodule. Idempotent: a patch that is
# already applied is skipped, and one that applies neither way fails the build loudly.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$here/aws-emulator"
if [ ! -f "$repo/Cargo.toml" ]; then
  echo "aws-emulator submodule missing; run: git submodule update --init" >&2
  exit 1
fi
for patch in "$here"/patches/*.patch; do
  if git -C "$repo" apply --reverse --check "$patch" 2>/dev/null; then
    continue
  fi
  git -C "$repo" apply "$patch"
  echo "applied $(basename "$patch")"
done
