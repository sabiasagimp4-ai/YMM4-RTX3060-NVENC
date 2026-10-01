#!/bin/bash
# Replaces the ci/nvenc-verify branch of a YMM4-dlls clone (DST) with the committed HEAD of this repository plus
# tools/ci/verify.yml, then commits. Push that branch to run the Windows checks against the YMM4 zip of the
# YMM4-dlls release (the YMM4 binaries are never added to either repository).
set -euo pipefail
SRC=${SRC:-$(cd "$(dirname "$0")/../.." && pwd)}
DST=${DST:?set DST to a clone of the private YMM4-dlls repository}
CI=$(cd "$(dirname "$0")" && pwd)
BRANCH=ci/nvenc-verify
commit=$(git -C "$SRC" rev-parse HEAD)
cd "$DST"
if git rev-parse --verify -q "$BRANCH" >/dev/null; then git checkout -q "$BRANCH";
elif git ls-remote --exit-code --heads origin "$BRANCH" >/dev/null 2>&1; then git fetch -q origin "$BRANCH" && git checkout -q -b "$BRANCH" FETCH_HEAD;
else git checkout -q --orphan "$BRANCH"; fi
git rm -rfq --ignore-unmatch . >/dev/null
find . -mindepth 1 -maxdepth 1 ! -name .git -exec rm -rf {} +
git -C "$SRC" archive "$commit" | tar -x -C "$DST"
mkdir -p .github/workflows
cp "$CI/verify.yml" .github/workflows/verify.yml
printf 'Snapshot of YMM4-RTX3060-NVENC %s (%s) for Windows verification only.\n' "$commit" "$(git -C "$SRC" log -1 --format=%s "$commit")" > CI_SOURCE.txt
git add -A
git commit -q -m "ci: verify YMM4-RTX3060-NVENC ${commit:0:7}" -m "${1:-Snapshot for the Windows verification workflow.}" || echo "nothing to commit"
git log --oneline -1
