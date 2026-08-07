#!/usr/bin/env bash
# Publishes docs/wiki/*.md to the repository's GitHub wiki.
#
# GitHub wikis are a separate git repository (<repo>.wiki.git) with no REST
# API, so this must run with credentials that can push to it (your own
# machine with gh/git auth). One-time: the wiki must exist — create any page
# once via the repo's Wiki tab if the clone below fails with "not found".
set -euo pipefail

REPO="${1:-pinqponq/pinqops}"
SRC="$(cd "$(dirname "$0")" && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

git clone "https://github.com/${REPO}.wiki.git" "$TMP/wiki"
cp "$SRC"/*.md "$TMP/wiki/"
cd "$TMP/wiki"
git add -A
if git diff --cached --quiet; then
  echo "wiki already up to date"
  exit 0
fi
git commit -m "docs: sync wiki from docs/wiki"
git push
echo "wiki published: https://github.com/${REPO}/wiki"
