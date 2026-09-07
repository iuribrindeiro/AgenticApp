#!/usr/bin/env bash
# Reports `open` declarations that can be deleted without breaking the build.
#
# F# has no compiler warning for an unused `open` (FS1182 covers values and parameters
# only) and no analyzer package provides one, so this asks the compiler directly: comment
# the line out, rebuild, and see whether it still compiles. Slow but exact - no false
# positives from heuristics.
#
# Exit 1 when anything is removable, so it can gate CI. Pass --fix to delete them.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# This script edits sources in place while it probes, so a build running alongside it
# sees a half-blanked file and fails for reasons that have nothing to do with the build.
# One at a time, and do not build while it runs.
LOCK="$ROOT/.config/.unused-opens.lock"
if ! mkdir "$LOCK" 2>/dev/null; then
  echo "another check-unused-opens run is in progress ($LOCK)"
  exit 1
fi
FIX=${1:-}
FOUND=0
declare -a REMOVABLE

project_for() {
  local dir; dir="$(dirname "$1")"
  while [ "$dir" != "$ROOT" ] && [ "$dir" != "/" ]; do
    local proj; proj=$(find "$dir" -maxdepth 1 -name "*.fsproj" | head -1)
    [ -n "$proj" ] && { echo "$proj"; return; }
    dir="$(dirname "$dir")"
  done
}

# A hard kill (SIGKILL, a CI timeout) outruns the trap below and leaves the file this
# run was probing blanked, next to its .orig. Recover from that before doing anything,
# so an interrupted run is self-healing rather than a broken tree.
for stale in $(find src tools -name "*.fs.orig" 2>/dev/null); do
  echo "recovering interrupted run: ${stale%.orig}"
  mv "$stale" "${stale%.orig}"
done

# Restore the file being edited if we are interrupted gracefully, and drop the lock.
# Both live in one handler because a second `trap ... EXIT` would silently replace the
# first, which is how the lock came to outlive every successful run and block the next.
CURRENT=""
cleanup() {
  [ -n "$CURRENT" ] && [ -f "$CURRENT.orig" ] && mv "$CURRENT.orig" "$CURRENT"
  rmdir "$LOCK" 2>/dev/null
  return 0
}
trap cleanup EXIT INT TERM

for file in $(find src tools -name "*.fs" ! -path "*/obj/*" ! -path "*/bin/*" | sort); do
  proj="$(project_for "$file")"
  [ -z "$proj" ] && continue

  # Line numbers of every `open`, deepest first so earlier edits do not shift them.
  lines=$(grep -n "^[[:space:]]*open " "$file" | cut -d: -f1 | sort -rn)
  [ -z "$lines" ] && continue

  for ln in $lines; do
    text=$(sed -n "${ln}p" "$file" | sed 's/^[[:space:]]*//')
    CURRENT="$file"
    cp "$file" "$file.orig"
    # Blank the line rather than deleting it, so numbering stays stable.
    sed -i '' "${ln}s|.*||" "$file"

    # Only compilation matters here; skip the format gate so each probe stays fast.
    if dotnet build "$proj" --nologo -v q -p:VerifyFormat=false >/dev/null 2>&1; then
      REMOVABLE+=("$file:$ln: $text")
      FOUND=1
      if [ "$FIX" = "--fix" ]; then
        rm -f "$file.orig"          # keep the blanked line, then squeeze it out below
        CURRENT=""
        continue
      fi
    fi

    mv "$file.orig" "$file"
    CURRENT=""
  done

  if [ "$FIX" = "--fix" ]; then
    # Collapse the blank lines left behind by removed opens.
    perl -0pi -e 's/\n\n(\n)/\n$1/g' "$file"
  fi
done

if [ "$FOUND" -eq 0 ]; then
  echo "unused opens: none"
  exit 0
fi

echo "unused opens: ${#REMOVABLE[@]}"
printf '  %s\n' "${REMOVABLE[@]}"
[ "$FIX" = "--fix" ] && { echo "removed. run: dotnet fantomas src tools && dotnet build AgenticApp.slnx"; exit 0; }
echo "run 'tools/check-unused-opens.sh --fix' to remove them"
exit 1
